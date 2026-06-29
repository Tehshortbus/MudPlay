namespace FujinTerm.Game.Combat;

/// <summary>
/// Phase 11 — counts the session's monster kills, experience earned, and
/// currency picked up vs. stashed/deposited for the Session Stats panel's
/// "Session Statistics" section, and keeps a short rolling history of kill /
/// experience events so the panel can draw kills/hour and exp/hour sparklines.
/// Produces a <see cref="SessionActivityStats"/> snapshot plus bucketed series
/// via <see cref="KillsPerHourSeries"/> and <see cref="ExperiencePerHourSeries"/>.
/// </summary>
/// <remarks>
/// <para>
/// Owns no source subscriptions — kills arrive from
/// <c>MonsterDeathWatcher.MonsterDied</c> and experience from a
/// <c>MessageRouter</c> pattern — so inputs are pushed in via the <c>Note*</c>
/// forwarders and <c>AppServices</c> wires the sources. This mirrors
/// <see cref="TimeAnalysisTracker"/> and keeps the tracker dependency-free behind
/// an injectable clock for unit tests. Every <c>Note*</c> call and
/// <see cref="Snapshot"/> runs on the marshalled dispatch thread (the sources all
/// fire there), so the counters are lock-free.
/// </para>
/// <para>
/// The headline <see cref="SessionActivityStats.MonstersKilled"/> /
/// <see cref="SessionActivityStats.ExperienceEarned"/> totals are plain running
/// counts that never decay. The kill / exp timestamp histories are separate and
/// pruned to <see cref="RateWindow"/> on every write, since they only feed the
/// rolling sparklines — old events leave the charts but stay in the totals.
/// </para>
/// </remarks>
public sealed class SessionActivityTracker
{
    /// <summary>How far back the kills/hour and exp/hour sparklines look. Event
    /// timestamps older than this are pruned from the rolling histories.</summary>
    public static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(60);

    private readonly Func<DateTimeOffset> _clock;

    private DateTimeOffset _sessionStart;
    private int _monstersKilled;
    private long _experienceEarned;
    private long _currencyCollected;
    private long _currencyStashed;

    // Kill timestamps within the rolling window, oldest first. Feeds only the
    // sparkline; the running total above is independent so the headline figure
    // never decays as old kills age out.
    private readonly List<DateTimeOffset> _recentKills = new();

    // Experience gains (timestamp + amount) within the rolling window, oldest
    // first. Same role as _recentKills but weighted by the exp amount, so the
    // exp/hour sparkline reflects how much was earned, not just how often.
    private readonly List<(DateTimeOffset At, long Amount)> _recentExp = new();

    /// <summary>Raised after any input updates the counters, so the Session Stats
    /// VM can refresh. Fires on the dispatch thread.</summary>
    public event Action? Changed;

    public SessionActivityTracker(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (static () => DateTimeOffset.Now);
        _sessionStart = _clock();
    }

    /// <summary>Record one monster kill (from <c>MonsterDeathWatcher.MonsterDied</c>).</summary>
    public void NoteKill()
    {
        DateTimeOffset now = _clock();
        _monstersKilled++;
        _recentKills.Add(now);
        PruneTo(now);
        Changed?.Invoke();
    }

    /// <summary>Add an experience gain (from the <c>UserGainExperience</c> line).
    /// Non-positive amounts are ignored.</summary>
    public void NoteExperience(int amount)
    {
        if (amount <= 0) return;
        DateTimeOffset now = _clock();
        _experienceEarned += amount;
        _recentExp.Add((now, amount));
        PruneTo(now);
        Changed?.Invoke();
    }

    /// <summary>Add currency picked up, as a copper value (auto-collected or
    /// manually <c>get</c>'d). Non-positive amounts are ignored.</summary>
    public void NoteCurrencyCollected(long copper)
    {
        if (copper <= 0) return;
        _currencyCollected += copper;
        Changed?.Invoke();
    }

    /// <summary>Add currency removed from the player this session — stash-room
    /// <c>hide</c>s and bank <c>dep</c>osits alike — as a copper value.
    /// Non-positive amounts are ignored.</summary>
    public void NoteCurrencyStashed(long copper)
    {
        if (copper <= 0) return;
        _currencyStashed += copper;
        Changed?.Invoke();
    }

    /// <summary>Point-in-time copy of the session's activity counters.</summary>
    public SessionActivityStats Snapshot() =>
        new(TimeOnline:        _clock() - _sessionStart,
            MonstersKilled:    _monstersKilled,
            ExperienceEarned:  _experienceEarned,
            CurrencyCollected: _currencyCollected,
            CurrencyStashed:   _currencyStashed);

    /// <summary>
    /// Kills/hour as a <paramref name="buckets"/>-point running-average curve
    /// across the rolling window — oldest point first, ready to feed
    /// <c>SparklineControl</c>. The window spans the last <see cref="RateWindow"/>,
    /// clamped to the session start so a young session fills the chart rather than
    /// trailing a long empty lead-in. Each point is the <i>cumulative</i> rate up
    /// to that slice's end (kills so far ÷ time so far), so the curve reads as the
    /// running kills/hour and its right-most point equals the headline
    /// <see cref="SessionActivityStats.KillsPerHour"/> the panel prints — the two
    /// are the same figure, not unrelated. (Per-slice instantaneous rates would
    /// spike to hundreds/hour off a single kill in a few-second slice, which is
    /// why we average cumulatively instead.)
    /// </summary>
    public IReadOnlyList<double> KillsPerHourSeries(int buckets) =>
        CumulativePerHour(buckets, _recentKills, static t => t, static _ => 1.0);

    /// <summary>
    /// Experience/hour as a running-average curve, shaped exactly like
    /// <see cref="KillsPerHourSeries"/> but weighted by the experience amount of
    /// each gain rather than a flat count — so the curve tracks the running
    /// exp/hour and its right-most point matches the headline
    /// <see cref="SessionActivityStats.ExperiencePerHour"/>.
    /// </summary>
    public IReadOnlyList<double> ExperiencePerHourSeries(int buckets) =>
        CumulativePerHour(buckets, _recentExp, static e => e.At, static e => e.Amount);

    // Shared bucketer for the running-average per-hour series: bins each event's
    // weight into equal time slices, then emits the CUMULATIVE rate at each
    // slice boundary (running weight ÷ running elapsed time). The final point is
    // total-weight ÷ window-span — the same figure SessionActivityStats prints —
    // so the chart and the headline number always agree. Generic over the event
    // shape so kills (flat weight 1) and experience (weight = amount) reuse the
    // identical windowing / pruning math.
    private IReadOnlyList<double> CumulativePerHour<T>(
        int buckets, List<T> events, Func<T, DateTimeOffset> at, Func<T, double> weight)
    {
        if (buckets < 1) return Array.Empty<double>();

        DateTimeOffset now = _clock();
        PruneTo(now);

        DateTimeOffset windowStart = _sessionStart;
        DateTimeOffset rollingStart = now - RateWindow;
        if (rollingStart > windowStart) windowStart = rollingStart;

        double spanHours = (now - windowStart).TotalHours;
        if (spanHours <= 0) return Array.Empty<double>();

        double bucketHours = spanHours / buckets;
        double[] perBucket = new double[buckets];
        foreach (T e in events)
        {
            DateTimeOffset t = at(e);
            if (t < windowStart) continue;
            int idx = (int)((t - windowStart).TotalHours / bucketHours);
            if (idx >= buckets) idx = buckets - 1; // an event landing exactly at 'now'
            if (idx < 0) idx = 0;
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

    /// <summary>Zero every counter and restart the session clock — called on the
    /// connect / character-switch boundary, matching the other Phase 11 trackers.</summary>
    public void Reset()
    {
        _sessionStart = _clock();
        _monstersKilled = 0;
        _experienceEarned = 0;
        _currencyCollected = 0;
        _currencyStashed = 0;
        _recentKills.Clear();
        _recentExp.Clear();
        Changed?.Invoke();
    }

    private void PruneTo(DateTimeOffset now)
    {
        DateTimeOffset cutoff = now - RateWindow;
        int dropKills = 0;
        while (dropKills < _recentKills.Count && _recentKills[dropKills] < cutoff) dropKills++;
        if (dropKills > 0) _recentKills.RemoveRange(0, dropKills);

        int dropExp = 0;
        while (dropExp < _recentExp.Count && _recentExp[dropExp].At < cutoff) dropExp++;
        if (dropExp > 0) _recentExp.RemoveRange(0, dropExp);
    }
}
