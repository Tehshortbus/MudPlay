namespace FujinTerm.Game.Combat;

// Counts the session's monster kills, experience earned, and currency picked up
// vs. stashed/deposited for the Session Stats panel's "Session Statistics"
// section, and keeps a history of kill / experience events so the panel can draw
// kills/hour and exp/hour sparklines. Produces a SessionActivityStats snapshot
// plus bucketed series via KillsPerHourSeries and ExperiencePerHourSeries.
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
// The kill / exp event histories are kept for the whole session (cleared only on
// Reset) and re-binned each snapshot, so the sparklines plot a running
// session-lifetime average whose right-most point equals the headline per-hour
// figure — the chart and the number the panel prints are always the same value.
// A rolling window was tried and dropped: once a session passed the window
// length the chart's right edge diverged from the headline rate, which read as a
// bug.
public sealed class SessionActivityTracker
{
    private readonly Func<DateTimeOffset> _clock;

    private DateTimeOffset _sessionStart;
    private int _monstersKilled;
    private long _experienceEarned;
    private long _currencyCollected;
    private long _currencyStashed;

    // Kill timestamps for the whole session, oldest first. Feeds only the
    // sparkline (re-binned each snapshot); the running total above is a separate
    // counter.
    private readonly List<DateTimeOffset> _killTimes = new();

    // Experience gains (timestamp + amount) for the whole session, oldest first.
    // Same role as _killTimes but weighted by the exp amount, so the exp/hour
    // sparkline reflects how much was earned, not just how often.
    private readonly List<(DateTimeOffset At, long Amount)> _expGains = new();

    // Raised after any input updates the counters, so the Session Stats VM can
    // refresh. Fires on the dispatch thread.
    public event Action? Changed;

    public SessionActivityTracker(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (static () => DateTimeOffset.Now);
        _sessionStart = _clock();
    }

    // Record one monster kill (from MonsterDeathWatcher.MonsterDied).
    public void NoteKill()
    {
        _monstersKilled++;
        _killTimes.Add(_clock());
        Changed?.Invoke();
    }

    // Add an experience gain (from the UserGainExperience line). Non-positive
    // amounts are ignored.
    public void NoteExperience(int amount)
    {
        if (amount <= 0) return;
        _experienceEarned += amount;
        _expGains.Add((_clock(), amount));
        Changed?.Invoke();
    }

    // Add currency picked up, as a copper value (auto-collected or manually
    // get'd). Non-positive amounts are ignored.
    public void NoteCurrencyCollected(long copper)
    {
        if (copper <= 0) return;
        _currencyCollected += copper;
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

    // Point-in-time copy of the session's activity counters.
    public SessionActivityStats Snapshot() =>
        new(TimeOnline:        _clock() - _sessionStart,
            MonstersKilled:    _monstersKilled,
            ExperienceEarned:  _experienceEarned,
            CurrencyCollected: _currencyCollected,
            CurrencyStashed:   _currencyStashed);

    // Kills/hour as a buckets-point running-average curve across the whole
    // session — oldest point first, ready to feed SparklineControl. Each point is
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
    // weight into equal time slices spanning session-start → now, then emits the
    // CUMULATIVE rate at each slice boundary (running weight ÷ running elapsed
    // time). The final point is total-weight ÷ session-span — the same figure
    // SessionActivityStats prints — so the chart and the headline number always
    // agree. Generic over the event shape so kills (flat weight 1) and experience
    // (weight = amount) reuse the identical windowing math.
    private IReadOnlyList<double> CumulativePerHour<T>(
        int buckets, List<T> events, Func<T, DateTimeOffset> at, Func<T, double> weight)
    {
        if (buckets < 1) return Array.Empty<double>();

        DateTimeOffset now = _clock();
        double spanHours = (now - _sessionStart).TotalHours;
        if (spanHours <= 0) return Array.Empty<double>();

        double bucketHours = spanHours / buckets;
        double[] perBucket = new double[buckets];
        foreach (T e in events)
        {
            int idx = (int)((at(e) - _sessionStart).TotalHours / bucketHours);
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

    // Zero every counter and restart the session clock — called on the connect /
    // character-switch boundary, matching the other session trackers.
    public void Reset()
    {
        _sessionStart = _clock();
        _monstersKilled = 0;
        _experienceEarned = 0;
        _currencyCollected = 0;
        _currencyStashed = 0;
        _killTimes.Clear();
        _expGains.Clear();
        Changed?.Invoke();
    }
}
