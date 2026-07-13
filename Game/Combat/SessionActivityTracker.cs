namespace FujinTerm.Game.Combat;

// Counts the session's monster kills, experience earned, and currency picked up
// vs. stashed/deposited for the Session Stats panel's "Session Statistics"
// section, and keeps a history of kill / experience / currency events so the
// panel can draw kills/hour and exp/hour sparklines and report every per-hour
// rate. Produces a SessionActivityStats snapshot plus bucketed series via
// KillsPerHourSeries and ExperiencePerHourSeries.
//
// Owns no source subscriptions — kills arrive from
// MonsterDeathWatcher.MonsterDied and experience from a MessageRouter pattern —
// so inputs are pushed in via the Note* forwarders and AppServices wires the
// sources. This mirrors TimeAnalysisTracker and keeps the tracker
// dependency-free behind an injectable clock for unit tests. Every Note* call
// and Snapshot runs on the marshalled dispatch thread (the sources all fire
// there), so the counters are lock-free.
//
// The headline MonstersKilled / ExperienceEarned totals are plain running counts.
// The per-hour rates are measured over a rolling window capped at four hours: a
// full night's loop stops averaging in kills from eight hours ago, so the rate
// reflects the recent pace, not the whole night diluted. The window's left edge
// is EffectiveStart = max(anchor, now - 4h) — the anchor while the session is
// still under four hours old, then a 4-hour trailing edge past that. Both the
// headline rate denominator AND the sparkline's bin origin use that same
// EffectiveStart, so the chart's right-most point still equals the headline
// per-hour figure — the chart and the number the panel prints stay the same
// value even after the window starts trailing.
//
// The event histories are trimmed to the 4-hour window eagerly inside each Note*
// (not only at Snapshot), because Snapshot runs only while the Session Stats
// window is open but kills / experience / currency fire all night regardless —
// so eager trimming is what keeps the lists bounded (a static ceiling) instead
// of growing until the panel is next opened.
//
// Lifetime totals and the rate window are decoupled. The totals accrue for the
// whole session; the rate window (its anchor and the kill / exp / currency
// histories) can be restarted on its own via ResetRates, which the Session Stats
// window binds to the Time Analysis section's reset. So resetting Time Analysis
// restarts every per-hour rate from now while the running totals the Session
// Statistics section shows stay put.
public sealed class SessionActivityTracker
{
    // Per-hour rates never average over more than this much history — a night-long
    // loop reports its recent pace, not the whole night blended together.
    private static readonly TimeSpan MaxRateWindow = TimeSpan.FromHours(4);

    private readonly Func<DateTimeOffset> _clock;

    // Lifetime totals (cleared only by a full Reset).
    private int _monstersKilled;
    private long _experienceEarned;
    private long _currencyCollected;
    private long _currencyStashed;

    // Rate-window anchor: the session/reset start. The window spans
    // [EffectiveStart(now), now] where EffectiveStart caps the look-back at
    // MaxRateWindow. ResetRates re-anchors this and clears the histories below
    // without touching the lifetime totals above.
    private DateTimeOffset _windowAnchor;

    // Kill timestamps within the rate window, oldest first. Feeds the sparkline
    // (re-binned each snapshot) and, by its count, the windowed kills/hour figure;
    // the lifetime total above is a separate counter.
    private readonly List<DateTimeOffset> _killTimes = new();

    // Experience gains (timestamp + amount) within the rate window, oldest first.
    // Same role as _killTimes but weighted by the exp amount, so the exp/hour
    // sparkline reflects how much was earned, not just how often.
    private readonly List<(DateTimeOffset At, long Amount)> _expGains = new();

    // Currency (copper) collected within the rate window, oldest first. A history
    // like the two above so currency/hour is measured over the same 4-hour window
    // rather than a running counter that could never be trimmed.
    private readonly List<(DateTimeOffset At, long Copper)> _currencyGains = new();

    // Raised after any input updates the counters, so the Session Stats VM can
    // refresh. Fires on the dispatch thread.
    public event Action? Changed;

    public SessionActivityTracker(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (static () => DateTimeOffset.Now);
        _windowAnchor = _clock();
    }

    // Record one monster kill (from MonsterDeathWatcher.MonsterDied).
    public void NoteKill()
    {
        _monstersKilled++;
        DateTimeOffset now = _clock();
        TrimToWindow(now);
        _killTimes.Add(now);
        Changed?.Invoke();
    }

    // Add an experience gain (from the UserGainExperience line). Non-positive
    // amounts are ignored.
    public void NoteExperience(int amount)
    {
        if (amount <= 0) return;
        _experienceEarned += amount;
        DateTimeOffset now = _clock();
        TrimToWindow(now);
        _expGains.Add((now, amount));
        Changed?.Invoke();
    }

    // Add currency picked up, as a copper value (auto-collected or manually
    // get'd). Non-positive amounts are ignored. Feeds both the lifetime total and
    // the rate window's currency history.
    public void NoteCurrencyCollected(long copper)
    {
        if (copper <= 0) return;
        _currencyCollected += copper;
        DateTimeOffset now = _clock();
        TrimToWindow(now);
        _currencyGains.Add((now, copper));
        Changed?.Invoke();
    }

    // Add currency removed from the player this session — stash-room hides and
    // bank deposits alike — as a copper value. Non-positive amounts are ignored.
    public void NoteCurrencyStashed(long copper)
    {
        if (copper <= 0) return;
        _currencyStashed += copper;
        Changed?.Invoke();
    }

    // Point-in-time copy of the session's activity counters: lifetime totals for
    // display plus the rate-window figures (windowed kills / experience /
    // currency and the window's elapsed time) the record divides into per-hour
    // rates. All three windowed figures derive from their histories, trimmed to
    // the 4-hour cap first so the reading never averages over stale events.
    public SessionActivityStats Snapshot()
    {
        DateTimeOffset now = _clock();
        TrimToWindow(now);

        long windowExperience = 0;
        foreach ((_, long amount) in _expGains) windowExperience += amount;
        long windowCurrency = 0;
        foreach ((_, long copper) in _currencyGains) windowCurrency += copper;

        return new(TimeOnline:        now - EffectiveStart(now),
            MonstersKilled:    _monstersKilled,
            ExperienceEarned:  _experienceEarned,
            CurrencyCollected: _currencyCollected,
            CurrencyStashed:   _currencyStashed,
            RateKills:         _killTimes.Count,
            RateExperience:    windowExperience,
            RateCurrency:      windowCurrency);
    }

    // Kills/hour as a buckets-point running-average curve across the current rate
    // window — oldest point first, ready to feed SparklineControl. Each point is
    // the cumulative rate up to that slice's end (kills so far ÷ time so far), so
    // the curve reads as the running kills/hour and its right-most point equals
    // the headline KillsPerHour the panel prints — the two are the same figure,
    // not unrelated. (Per-slice instantaneous rates would spike to hundreds/hour
    // off a single kill in a few-second slice, which is why we average
    // cumulatively instead.)
    public IReadOnlyList<double> KillsPerHourSeries(int buckets) =>
        CumulativePerHour(buckets, _killTimes, static t => t, static _ => 1.0);

    // Experience/hour as a running-average curve, shaped exactly like
    // KillsPerHourSeries but weighted by the experience amount of each gain
    // rather than a flat count — so the curve tracks the running exp/hour and its
    // right-most point matches the headline ExperiencePerHour.
    public IReadOnlyList<double> ExperiencePerHourSeries(int buckets) =>
        CumulativePerHour(buckets, _expGains, static e => e.At, static e => e.Amount);

    // Shared bucketer for the running-average per-hour series: bins each event's
    // weight into equal time slices spanning EffectiveStart → now, then emits the
    // CUMULATIVE rate at each slice boundary (running weight ÷ running elapsed
    // time). The final point is total-weight ÷ window-span — the same figure
    // SessionActivityStats prints, because both use EffectiveStart as the origin —
    // so the chart and the headline number always agree even once the window
    // starts trailing at four hours. Generic over the event shape so kills (flat
    // weight 1) and experience (weight = amount) reuse the identical windowing math.
    private IReadOnlyList<double> CumulativePerHour<T>(
        int buckets, List<T> events, Func<T, DateTimeOffset> at, Func<T, double> weight)
    {
        if (buckets < 1) return Array.Empty<double>();

        DateTimeOffset now = _clock();
        TrimToWindow(now);
        DateTimeOffset start = EffectiveStart(now);
        double spanHours = (now - start).TotalHours;
        if (spanHours <= 0) return Array.Empty<double>();

        double bucketHours = spanHours / buckets;
        double[] perBucket = new double[buckets];
        foreach (T e in events)
        {
            int idx = (int)((at(e) - start).TotalHours / bucketHours);
            if (idx >= buckets) idx = buckets - 1; // an event landing exactly at 'now'
            if (idx < 0) idx = 0;                   // guard against clock skew
            perBucket[idx] += weight(e);
        }

        double[] series = new double[buckets];
        double cumWeight = 0;
        for (int i = 0; i < buckets; i++)
        {
            cumWeight += perBucket[i];
            series[i] = cumWeight / (bucketHours * (i + 1)); // running weight ÷ running hours
        }
        return series;
    }

    // Left edge of the rate window: the later of the session/reset anchor and the
    // 4-hour trailing cutoff. Used as BOTH the headline rate denominator's start
    // and the sparkline's bin origin, which is what keeps the chart's right edge
    // equal to the headline figure once the window begins trailing.
    private DateTimeOffset EffectiveStart(DateTimeOffset now)
    {
        DateTimeOffset cutoff = now - MaxRateWindow;
        return cutoff > _windowAnchor ? cutoff : _windowAnchor;
    }

    // Drop every history entry older than the 4-hour cap. Called eagerly from each
    // Note* (kills / experience / currency all fire all night, even with the panel
    // closed) so the lists stay bounded rather than growing until Snapshot runs.
    private void TrimToWindow(DateTimeOffset now)
    {
        DateTimeOffset cutoff = now - MaxRateWindow;
        TrimFront(_killTimes, static t => t, cutoff);
        TrimFront(_expGains, static e => e.At, cutoff);
        TrimFront(_currencyGains, static c => c.At, cutoff);
    }

    // Remove leading entries older than the cutoff from an oldest-first list.
    // Front-only removal is valid because every Note* appends at 'now', so the
    // lists are always in chronological order.
    private static void TrimFront<T>(List<T> events, Func<T, DateTimeOffset> at, DateTimeOffset cutoff)
    {
        int drop = 0;
        while (drop < events.Count && at(events[drop]) < cutoff) drop++;
        if (drop > 0) events.RemoveRange(0, drop);
    }

    // Zero every counter — lifetime totals and the rate window alike — and
    // restart the clock. Called on the connect / character-switch boundary and by
    // the Session Statistics section's reset, matching the other session trackers.
    public void Reset()
    {
        _monstersKilled = 0;
        _experienceEarned = 0;
        _currencyCollected = 0;
        _currencyStashed = 0;
        ResetRates();
    }

    // Restart only the per-hour rate window: re-anchor the rate clock and clear
    // the kill / experience / currency histories feeding the rates and sparklines
    // — leaving the lifetime totals (kills, experience, currency collected /
    // stashed) intact. Bound to the Time Analysis section's reset: the per-hour
    // figures are measured over the session time that panel represents, so
    // restarting that time restarts every rate from now without discarding the
    // running tallies the Session Statistics section shows.
    public void ResetRates()
    {
        _windowAnchor = _clock();
        _killTimes.Clear();
        _expGains.Clear();
        _currencyGains.Clear();
        Changed?.Invoke();
    }
}
