using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game.Combat;

// Aggregates combat-line observations into RoundSummary records on a ~5-second
// cadence. Keeps a ring buffer of the last 50 rounds and emits RoundComplete at
// each round boundary.
//
// Round boundary = 5+ seconds since the current round started, observed at the
// next damage line. *Combat Off* closes the current round explicitly via
// MarkCombatEnded. Timer-free: a stale round (no damage lines after combat ends)
// closes on the next combat encounter, on an explicit MarkCombatEnded, or on
// Reset.
//
// Subscribers: CastingDirector reads per-round HP/MA deltas to score routine
// heals; CombatSessionTracker aggregates into session observed-accuracy
// counters.
//
// The opt-in combat-{sessionStart}.log file (Settings.Other →
// WriteCombatRoundTrace) is wired here: when the shouldWriteTrace delegate
// returns true on round close, the tracker also emits a one-line summary to its
// DebugLogWriter. The delegate is queried per round so the user can toggle the
// setting mid-session and the next round reflects the new value.
public sealed class RoundDamageTracker : IDisposable
{
    // LogService category — appears as [Round] info-severity rows on each closed
    // round.
    public const string LogCategory = "Round";

    // 5-second canonical MajorMUD round length.
    public static readonly TimeSpan RoundDuration = TimeSpan.FromSeconds(5);

    // Capacity of the in-memory ring buffer of recent rounds.
    public const int RingCapacity = 50;

    private readonly PlayerState _state;
    private readonly LogService? _log;
    private readonly Func<bool> _shouldWriteTrace;

    private readonly IDisposable _userHitsSub;
    private readonly IDisposable _mobHitsSub;
    private readonly IDisposable _mobMissesSub;
    private readonly IDisposable _combatStatusSub;

    private readonly Queue<RoundSummary> _ring = new(RingCapacity);
    private readonly object _ringLock = new();

    private DebugLogWriter? _trace;
    private RoundAccumulator? _current;
    private int _roundCounter;
    private bool _disposed;

    // Fired after each round closes, with the summary payload. Subscribers run on
    // the MessageRouter's marshalled thread.
    public event Action<RoundSummary>? RoundComplete;

    // Snapshot of the ring buffer, oldest first.
    public IReadOnlyList<RoundSummary> Recent
    {
        get { lock (_ringLock) { return _ring.ToArray(); } }
    }

    // Number of rounds closed since the last Reset.
    public int RoundCount => _roundCounter;

    public RoundDamageTracker(
        MessageRouter router,
        PlayerState state,
        LogService? log = null,
        Func<bool>? shouldWriteTrace = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
        _log   = log;
        _shouldWriteTrace = shouldWriteTrace ?? (static () => false);

        _userHitsSub     = router.Subscribe(KnownPatterns.UserHits,    OnUserHits);
        _mobHitsSub      = router.Subscribe(KnownPatterns.MobHits,     OnMobHits);
        _mobMissesSub    = router.Subscribe(KnownPatterns.MobMisses,   OnMobMisses);
        _combatStatusSub = router.Subscribe(KnownPatterns.CombatStatus, OnCombatStatus);
    }

    private void OnUserHits(MatchResult match)
    {
        // The KnownPatterns.UserHits regex is intentionally broad and
        // ALSO matches "The {monster} {verb} you for N damage!" — the
        // same line MobHits fires on. Guard against double-counting by
        // rejecting lines whose first capture (the source name) is the
        // definite article. A real player name is never "The"; mob
        // damage on us routes through OnMobHits with the canonical
        // "The {target}" prefix.
        if (match.Groups.Count > 0 &&
            string.Equals(match.Groups[0], "The", StringComparison.OrdinalIgnoreCase))
            return;

        int dmg = TryParseDamageGroup(match, groupIndex: 2);
        StartOrAppend(damageDealt: dmg, damageTaken: 0, miss: false);
    }

    private void OnMobHits(MatchResult match)
    {
        int dmg = TryParseDamageGroup(match, groupIndex: 1);
        StartOrAppend(damageDealt: 0, damageTaken: dmg, miss: false);
    }

    private void OnMobMisses(MatchResult _)
    {
        StartOrAppend(damageDealt: 0, damageTaken: 0, miss: true);
    }

    private void OnCombatStatus(MatchResult match)
    {
        if (match.Groups.Count == 0) return;
        string status = match.Groups[0];
        if (string.Equals(status, "Off", StringComparison.OrdinalIgnoreCase))
            MarkCombatEnded();
    }

    // Append a damage observation to the current round, starting a new round when
    // none is open or when the current one has been open for at least
    // RoundDuration.
    private void StartOrAppend(int damageDealt, int damageTaken, bool miss)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        if (_current is null || now - _current.StartedAt >= RoundDuration)
        {
            if (_current is not null) CloseCurrent(now);
            _current = new RoundAccumulator
            {
                StartedAt = now,
                HpStart   = _state.Hp,
                MaStart   = _state.Ma,
            };
        }
        if (damageDealt > 0) _current.DamageDealt += damageDealt;
        if (damageTaken > 0) _current.DamageTaken += damageTaken;
        if (damageDealt > 0 || damageTaken > 0) _current.Hits++;
        if (miss) _current.Misses++;
    }

    // Explicitly close the current round (no-op when none is open) — driven
    // internally by *Combat Off* and exposed publicly so other subsystems (e.g. a
    // death watcher) can mark the end of a fight without waiting for the next
    // round-tick.
    public void MarkCombatEnded()
    {
        if (_current is null) return;
        CloseCurrent(DateTimeOffset.Now);
    }

    // Reset the ring buffer + round counter. Called on connect to BBS / character
    // switch — same boundary CombatSessionTracker uses for its session
    // aggregates.
    public void Reset()
    {
        _current = null;
        _roundCounter = 0;
        lock (_ringLock) _ring.Clear();
    }

    private void CloseCurrent(DateTimeOffset endedAt)
    {
        if (_current is null) return;
        _roundCounter++;
        RoundSummary summary = new(
            RoundNumber:  _roundCounter,
            StartedAt:    _current.StartedAt,
            EndedAt:      endedAt,
            DamageDealt:  _current.DamageDealt,
            DamageTaken:  _current.DamageTaken,
            Hits:         _current.Hits,
            Misses:       _current.Misses,
            HpBefore:     _current.HpStart,
            HpAfter:      _state.Hp,
            MaBefore:     _current.MaStart,
            MaAfter:      _state.Ma);

        lock (_ringLock)
        {
            if (_ring.Count >= RingCapacity) _ring.Dequeue();
            _ring.Enqueue(summary);
        }
        _current = null;

        _log?.Combat(LogCategory,
            $"n={summary.RoundNumber} dmgDealt={summary.DamageDealt} " +
            $"dmgTaken={summary.DamageTaken} hits={summary.Hits} misses={summary.Misses} " +
            $"hpAfter={summary.HpAfter} maAfter={summary.MaAfter}");

        if (_shouldWriteTrace())
        {
            _trace ??= new DebugLogWriter("combat");
            _trace.WriteLine(
                $"round n={summary.RoundNumber} " +
                $"startedAt={summary.StartedAt:HH:mm:ss.fff} " +
                $"endedAt={summary.EndedAt:HH:mm:ss.fff} " +
                $"dmgDealt={summary.DamageDealt} dmgTaken={summary.DamageTaken} " +
                $"hits={summary.Hits} misses={summary.Misses} " +
                $"hp={summary.HpBefore}->{summary.HpAfter} " +
                $"ma={summary.MaBefore}->{summary.MaAfter}");
        }
        else if (_trace is not null)
        {
            // User toggled WriteCombatRoundTrace off mid-session — close
            // the open trace file. Re-opens automatically when the
            // toggle flips back on.
            _trace.Dispose();
            _trace = null;
        }

        RoundComplete?.Invoke(summary);
    }

    // Read an integer capture by positional index, defaulting to 0 when the group
    // is missing or non-numeric. Matches the MessageRouter's positional Groups
    // indexing convention — group 0 = first capture (the regex's (?<source>...)
    // for UserHits).
    private static int TryParseDamageGroup(MatchResult match, int groupIndex)
    {
        if (match.Groups.Count <= groupIndex) return 0;
        return int.TryParse(match.Groups[groupIndex], out int v) ? v : 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _userHitsSub.Dispose();
        _mobHitsSub.Dispose();
        _mobMissesSub.Dispose();
        _combatStatusSub.Dispose();
        _trace?.Dispose();
    }

    private sealed class RoundAccumulator
    {
        public DateTimeOffset StartedAt;
        public int DamageDealt;
        public int DamageTaken;
        public int Hits;
        public int Misses;
        public int HpStart;
        public int MaStart;
    }
}
