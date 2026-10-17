namespace MudPlay.Game.Combat;

// Counts the session's monster kills, experience earned, and the copper and
// items collected / deposited-or-sold / stashed for the Session Stats panel's
// "Session Statistics" section, plus how often a sneak held on entering a room
// and how long a walk step takes. Keeps a history of kill / experience /
// currency events so the panel can draw kills/hour and exp/hour sparklines and
// report every per-hour rate. Produces a SessionActivityStats snapshot plus
// bucketed series via KillsPerHourSeries and ExperiencePerHourSeries.
//
// Owns no source subscriptions — kills arrive from
// MonsterDeathWatcher.MonsterDied, experience from a MessageRouter pattern,
// items from InventoryManager, sneak entries from StealthManager and steps from
// RoomTracker — so inputs are pushed in via the Note* forwarders and AppServices
// wires the sources. This mirrors TimeAnalysisTracker and keeps the tracker
// dependency-free behind an injectable clock. Every Note* call
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
// histories) is bounded by MaxRateWindow, so a long session's rates follow its
// recent pace while the totals keep the whole session; the Session Statistics
// section's reset (ResetTotals) restarts both.
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
    private long _coinsCollected;
    private long _coinsStashed;
    private long _currencyStashed;
    private long _currencyDeposited;
    private int _itemsCollected;
    private int _itemsSold;
    private int _itemsStashed;
    private int _sneakEntries;
    private int _sneakHeld;
    private int _disarmAttempts;
    private int _trapsDisarmed;
    private int _steps;
    private TimeSpan _stepTime;

    // When the walk step now in flight went out; null between steps. Only the
    // send-to-arrival span counts, so time stopped between steps (a fight, a rest,
    // a gate) never lands in the average.
    private DateTimeOffset? _stepSentAt;

    // Rate-window anchor: the session/reset start. The window spans
    // [EffectiveStart(now), now] where EffectiveStart caps the look-back at
    // MaxRateWindow. ResetTotals re-anchors this and clears the histories below.
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
    private readonly List<(DateTimeOffset At, long Copper, long Coins)> _currencyGains = new();

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

    // Add currency picked up (auto-collected or manually get'd): its copper value
    // and how many coins it was. Non-positive amounts are ignored. Feeds both the
    // lifetime totals and the rate window's currency history.
    public void NoteCurrencyCollected(long copper, long coins)
    {
        if (copper <= 0) return;
        _currencyCollected += copper;
        _coinsCollected += Math.Max(0, coins);
        DateTimeOffset now = _clock();
        TrimToWindow(now);
        _currencyGains.Add((now, copper, Math.Max(0, coins)));
        Changed?.Invoke();
    }

    // Add currency hidden in a stash room this session, as a copper value.
    // Non-positive amounts are ignored.
    public void NoteCurrencyStashed(long copper, long coins)
    {
        if (copper <= 0) return;
        _currencyStashed += copper;
        _coinsStashed += Math.Max(0, coins);
        Changed?.Invoke();
    }

    // Add currency banked this session, as a copper value. Non-positive amounts
    // are ignored. Shares the Deposit/Sold total with sale proceeds.
    public void NoteCurrencyDeposited(long copper)
    {
        if (copper <= 0) return;
        _currencyDeposited += copper;
        Changed?.Invoke();
    }

    // A confirmed shop sale: count items sold for copper. The proceeds join bank
    // deposits in the Deposit/Sold copper total.
    public void NoteSale(int count, long copper)
    {
        if (count <= 0 && copper <= 0) return;
        if (count > 0) _itemsSold += count;
        if (copper > 0) _currencyDeposited += copper;
        Changed?.Invoke();
    }

    // Add items picked up off the floor. Non-positive counts are ignored.
    public void NoteItemsCollected(int count)
    {
        if (count <= 0) return;
        _itemsCollected += count;
        Changed?.Invoke();
    }

    // Add items hidden in a stash room. Non-positive counts are ignored.
    public void NoteItemsStashed(int count)
    {
        if (count <= 0) return;
        _itemsStashed += count;
        Changed?.Invoke();
    }

    // One room entered while sneaking: held = the room confirmed the sneak, false
    // = it broke on the way in (loud or silent).
    public void NoteSneakEntry(bool held)
    {
        _sneakEntries++;
        if (held) _sneakHeld++;
        Changed?.Invoke();
    }

    // One disarm trap attempt settled (from TrapDisarmManager.DisarmAttempted).
    public void NoteDisarmAttempt(bool disarmed)
    {
        _disarmAttempts++;
        if (disarmed) _trapsDisarmed++;
        Changed?.Invoke();
    }

    // A walk step's move just went out — starts its timer. A second send before
    // the first arrives restarts it from the later send.
    public void NoteStepSent() => _stepSentAt = _clock();

    // The step in flight reached its new room — adds its send-to-arrival time to
    // the walk average. No-op when no step is being timed.
    public void NoteStepArrived()
    {
        if (_stepSentAt is not { } sent) return;
        _stepSentAt = null;
        TimeSpan took = _clock() - sent;
        if (took < TimeSpan.Zero) return;
        _steps++;
        _stepTime += took;
        Changed?.Invoke();
    }

    // The step in flight didn't land (refused, re-looked, lost the room) — drop
    // its timer without counting it. True when a timed step was dropped.
    public bool NoteStepAbandoned()
    {
        if (_stepSentAt is null) return false;
        _stepSentAt = null;
        return true;
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
        long windowCurrency = 0, windowCoins = 0;
        foreach ((_, long copper, long coins) in _currencyGains)
        {
            windowCurrency += copper;
            windowCoins += coins;
        }

        return new(TimeOnline:        now - EffectiveStart(now),
            MonstersKilled:    _monstersKilled,
            ExperienceEarned:  _experienceEarned,
            CurrencyCollected: _currencyCollected,
            CoinsCollected:    _coinsCollected,
            CurrencyStashed:   _currencyStashed,
            CoinsStashed:      _coinsStashed,
            RateKills:         _killTimes.Count,
            RateExperience:    windowExperience,
            RateCurrency:      windowCurrency,
            RateCoins:         windowCoins,
            CurrencyDeposited: _currencyDeposited,
            ItemsCollected:    _itemsCollected,
            ItemsSold:         _itemsSold,
            ItemsStashed:      _itemsStashed,
            SneakEntries:      _sneakEntries,
            SneakHeld:         _sneakHeld,
            DisarmAttempts:    _disarmAttempts,
            TrapsDisarmed:     _trapsDisarmed,
            Steps:             _steps,
            StepTime:          _stepTime);
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

    // Zero every counter — lifetime totals, sneak, disarm and walk figures, and the rate
    // window alike — and restart the clock. Called on the connect /
    // character-switch boundary, matching the other session trackers.
    public void Reset()
    {
        ResetTotals();
        ResetMovement();
    }

    // The Session Statistics section's reset: its running totals and their rates.
    public void ResetTotals()
    {
        _monstersKilled = 0;
        _experienceEarned = 0;
        _currencyCollected = 0;
        _coinsCollected = 0;
        _coinsStashed = 0;
        _currencyStashed = 0;
        _currencyDeposited = 0;
        _itemsCollected = 0;
        _itemsSold = 0;
        _itemsStashed = 0;
        // The rate window restarts with them: the rate clock and the kill /
        // experience / currency histories feeding the rates and sparklines.
        _windowAnchor = _clock();
        _killTimes.Clear();
        _expGains.Clear();
        _currencyGains.Clear();
        Changed?.Invoke();
    }

    // The sneak, disarm and walk figures, shown under Time Analysis and reset with it.
    public void ResetMovement()
    {
        _sneakEntries = 0;
        _sneakHeld = 0;
        _disarmAttempts = 0;
        _trapsDisarmed = 0;
        _steps = 0;
        _stepTime = TimeSpan.Zero;
        _stepSentAt = null;
        Changed?.Invoke();
    }

}
