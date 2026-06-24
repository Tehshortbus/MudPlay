namespace FujinTerm.Game.Combat;

/// <summary>
/// Phase 11 — counts the session's monster kills and experience earned for the
/// Session Stats panel's "Session Statistics" section, and keeps a short rolling
/// history of kill timestamps so the panel can draw a kills/hour sparkline.
/// Produces a <see cref="SessionActivityStats"/> snapshot plus a bucketed
/// kills/hour series via <see cref="KillsPerHourSeries"/>.
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
/// The headline <see cref="SessionActivityStats.MonstersKilled"/> total is a
/// plain running count that never decays. The kill-timestamp history is separate
/// and pruned to <see cref="KillRateWindow"/> on every write, since it only feeds
/// the rolling sparkline — old kills leave the chart but stay in the total.
/// </para>
/// </remarks>
public sealed class SessionActivityTracker
{
    /// <summary>How far back the kills/hour sparkline looks. Kill timestamps
    /// older than this are pruned from the rolling history.</summary>
    public static readonly TimeSpan KillRateWindow = TimeSpan.FromMinutes(60);

    private readonly Func<DateTimeOffset> _clock;

    private DateTimeOffset _sessionStart;
    private int _monstersKilled;
    private long _experienceEarned;

    // Kill timestamps within the rolling window, oldest first. Feeds only the
    // sparkline; the running total above is independent so the headline figure
    // never decays as old kills age out.
    private readonly List<DateTimeOffset> _recentKills = new();

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
        _experienceEarned += amount;
        Changed?.Invoke();
    }

    /// <summary>Point-in-time copy of the session's activity counters.</summary>
    public SessionActivityStats Snapshot() =>
        new(TimeOnline:       _clock() - _sessionStart,
            MonstersKilled:   _monstersKilled,
            ExperienceEarned: _experienceEarned);

    /// <summary>
    /// Kills/hour split into <paramref name="buckets"/> equal time slices across
    /// the rolling window — oldest slice first, ready to feed
    /// <c>SparklineControl</c>. The window spans the last
    /// <see cref="KillRateWindow"/>, clamped to the session start so a young
    /// session fills the chart rather than trailing a long empty lead-in. Each
    /// slice's value is its kill count converted to a per-hour rate; since the
    /// slices are equal-width that scaling is uniform and doesn't change the
    /// rendered shape, but keeps the series true to its "kills/hour" name.
    /// </summary>
    public IReadOnlyList<double> KillsPerHourSeries(int buckets)
    {
        if (buckets < 1) return Array.Empty<double>();

        DateTimeOffset now = _clock();
        PruneTo(now);

        DateTimeOffset windowStart = _sessionStart;
        DateTimeOffset rollingStart = now - KillRateWindow;
        if (rollingStart > windowStart) windowStart = rollingStart;

        double spanHours = (now - windowStart).TotalHours;
        if (spanHours <= 0) return Array.Empty<double>();

        double bucketHours = spanHours / buckets;
        double[] series = new double[buckets];
        foreach (DateTimeOffset t in _recentKills)
        {
            if (t < windowStart) continue;
            int idx = (int)((t - windowStart).TotalHours / bucketHours);
            if (idx >= buckets) idx = buckets - 1; // a kill landing exactly at 'now'
            if (idx < 0) idx = 0;
            series[idx] += 1;
        }
        for (int i = 0; i < buckets; i++) series[i] /= bucketHours;
        return series;
    }

    /// <summary>Zero every counter and restart the session clock — called on the
    /// connect / character-switch boundary, matching the other Phase 11 trackers.</summary>
    public void Reset()
    {
        _sessionStart = _clock();
        _monstersKilled = 0;
        _experienceEarned = 0;
        _recentKills.Clear();
        Changed?.Invoke();
    }

    private void PruneTo(DateTimeOffset now)
    {
        DateTimeOffset cutoff = now - KillRateWindow;
        int drop = 0;
        while (drop < _recentKills.Count && _recentKills[drop] < cutoff) drop++;
        if (drop > 0) _recentKills.RemoveRange(0, drop);
    }
}
