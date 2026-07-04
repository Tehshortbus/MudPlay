using System.ComponentModel;

namespace FujinTerm.Game;

// Watches PlayerState and runs four independent regen cycles whose anchors
// and intervals reflect what the MajorMUD server actually does:
//
//   HpNatural — 30 s. Always running once anchored on the first observed HP
//     uptick. Per-tick amount = HPRegen / 3.
//   HpRest    — 20 s. Starts the moment the user enters Resting, stops when
//     they leave. Anchor is independent of the natural cycle. Per-tick
//     amount = HPRegen.
//   MpNatural — 30 s. Always running once anchored. Per-tick amount = MPRegen.
//   MpMedi    — 10 s. Starts on entering Meditating, stops on leaving.
//     Per-tick amount = MeditateRate.
//
// The natural cycle and the bonus cycle can be (and usually are)
// desynchronised — the natural anchors at first observation; the bonus
// anchors when the user types rest / meditate. The status bar shows them as
// parallel countdowns.
//
// The two natural cycles (HP + MA) fire on the same server pulse, however —
// empirically verified. Any observation that anchors one mirrors the anchor
// onto the other so a max-HP / max-MA character still has a live countdown
// driven by whichever stream is moving.
//
// The intervals quoted above are the Stock cadence. On ParaMud / Paradigm the
// server splits each cycle's amount into thirds on a faster grid, so the
// observable cadence differs — SetRealm re-seeds every cycle from the active
// RealmRegenProfile (wired to GameDataCache.ActiveSetChanged in AppServices).
// Cycles default to the Stock profile until told otherwise.
public sealed class RegenTracker : IDisposable
{
    private readonly PlayerState _state;
    private readonly Func<DateTimeOffset> _clock;

    private DateTimeOffset? _lastArtifactAt;
    private int _lastHp;
    private int _lastMa;
    private DateTimeOffset? _lastHpTickAt;
    private DateTimeOffset? _lastMaTickAt;
    private bool _hpBaselineSet;
    private bool _maBaselineSet;
    private bool _disposed;

    private RealmRegenProfile _profile = RealmRegenProfile.Stock;

    public RegenCycle HpNatural { get; } = new("HP natural", RegenConstants.SeedStandingInterval);
    public RegenCycle HpRest    { get; } = new("HP rest",    RegenConstants.SeedRestingInterval);
    public RegenCycle MpNatural { get; } = new("MP natural", RegenConstants.SeedStandingInterval);
    public RegenCycle MpMedi    { get; } = new("MP medi",    RegenConstants.SeedMeditatingInterval);

    // Fired after an observed HP uptick that passes the artifact filter.
    public event Action<RegenSample>? HpTickObserved;

    // Fired after an observed MA uptick that passes the artifact filter.
    public event Action<RegenSample>? MaTickObserved;

    public RegenTracker(PlayerState state, Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _state.PropertyChanged += OnPlayerStateChanged;
    }

    // Time-to-next HP natural tick, or null before first observation.
    public TimeSpan? GetTimeToNextHpNaturalTick() => HpNatural.GetTimeToNext(_clock());

    // Time-to-next HP rest tick, or null when not resting.
    public TimeSpan? GetTimeToNextHpRestTick() => HpRest.GetTimeToNext(_clock());

    // Time-to-next MP natural tick, or null before first observation.
    public TimeSpan? GetTimeToNextMpNaturalTick() => MpNatural.GetTimeToNext(_clock());

    // Time-to-next MP meditate tick, or null when not meditating.
    public TimeSpan? GetTimeToNextMpMediTick() => MpMedi.GetTimeToNext(_clock());

    // Re-seed every cycle's tick cadence for the given realm family (see
    // RealmRegenProfile). Called once at wire-up and again on every
    // GameDataCache.ActiveSetChanged. Idempotent — re-applying the same realm
    // just re-asserts the same intervals.
    public void SetRealm(RealmType realm)
    {
        _profile = RealmRegenProfile.For(realm);
        HpNatural.Reseed(_profile.StandingInterval);
        MpNatural.Reseed(_profile.StandingInterval);
        HpRest.Reseed(_profile.RestingInterval);
        MpMedi.Reseed(_profile.MeditatingInterval);
    }

    // Mark the moment as an artifact (heal / drink / etc.) so subsequent
    // up-deltas drop.
    public void RecordArtifact() => _lastArtifactAt = _clock();

    // Reset every cycle's amount stat + stop bonus cycles. Natural cycles keep
    // their anchor.
    public void ResetAll()
    {
        HpNatural.Stat.Reset();
        HpRest.Stat.Reset();
        MpNatural.Stat.Reset();
        MpMedi.Stat.Reset();
    }

    private void OnPlayerStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerState.Hp):       ConsiderHp();        break;
            case nameof(PlayerState.Ma):       ConsiderMa();        break;
            case nameof(PlayerState.Position): ApplyPositionChange(); break;
        }
    }

    private void ConsiderHp()
    {
        int current = _state.Hp;
        DateTimeOffset now = _clock();

        if (!_hpBaselineSet)
        {
            _lastHp = current;
            _hpBaselineSet = true;
            return;
        }

        int delta = current - _lastHp;
        _lastHp = current;

        if (delta <= 0) return;                  // damage / no change.
        if (IsInArtifactWindow(now)) return;     // heal-shaped event recently.

        // Credit whichever active HP cycle is closer to its boundary. If
        // both rest + natural look due, both get advanced (a 60 s mark
        // while resting fires both simultaneously).
        bool restClaimed = ClaimIfDue(HpRest, now, delta);
        bool natClaimed  = ClaimIfDue(HpNatural, now, delta);
        if (!restClaimed && !natClaimed)
        {
            // No active cycle was due — anchor HpNatural so it starts the
            // 30 s cadence from this observation. First-time path.
            HpNatural.RecordObservation(now, delta);
            natClaimed = true;
        }
        // Natural HP and natural MA fire on the same server pulse —
        // empirically verified. Sync MpNatural so its countdown stays
        // valid even when MA sits at max and never upticks.
        if (natClaimed) MpNatural.Start(now);

        TimeSpan sinceLast = _lastHpTickAt is { } prevTick ? now - prevTick : TimeSpan.Zero;
        _lastHpTickAt = now;
        HpTickObserved?.Invoke(new RegenSample(now, delta, sinceLast, _state.Position));
    }

    private void ConsiderMa()
    {
        int current = _state.Ma;
        DateTimeOffset now = _clock();

        if (!_maBaselineSet)
        {
            _lastMa = current;
            _maBaselineSet = true;
            return;
        }

        int delta = current - _lastMa;
        _lastMa = current;

        if (delta <= 0) return;
        if (IsInArtifactWindow(now)) return;

        bool mediClaimed = ClaimIfDue(MpMedi, now, delta);
        bool natClaimed  = ClaimIfDue(MpNatural, now, delta);
        if (!mediClaimed && !natClaimed)
        {
            MpNatural.RecordObservation(now, delta);
            natClaimed = true;
        }
        // Mirror onto HpNatural — same pulse. Lets a max-HP character
        // still see a live HP countdown driven by observed MA ticks.
        if (natClaimed) HpNatural.Start(now);

        TimeSpan sinceLast = _lastMaTickAt is { } prevTick ? now - prevTick : TimeSpan.Zero;
        _lastMaTickAt = now;
        MaTickObserved?.Invoke(new RegenSample(now, delta, sinceLast, _state.Position));
    }

    // If cycle is active and the now-instant is at or past its next-tick
    // boundary (with a small grace), record the observation and return true.
    private static bool ClaimIfDue(RegenCycle cycle, DateTimeOffset now, double delta)
    {
        if (cycle.Anchor is not { } anchor) return false;
        TimeSpan elapsed = now - anchor;
        TimeSpan grace = TimeSpan.FromMilliseconds(750);
        if (elapsed + grace < cycle.Interval) return false;
        cycle.RecordObservation(now, delta);
        return true;
    }

    // Start / stop the rest + medi bonus cycles on position transitions.
    // Leaving the position before the cycle's interval elapses cancels the
    // pending tick outright (no partial credit) — the server only fires the
    // rest / medi tick if the player stayed in the position for the full
    // interval. Re-entering re-anchors from the new transition.
    private void ApplyPositionChange()
    {
        DateTimeOffset now = _clock();
        if (_state.Position == PlayerPosition.Resting && !HpRest.IsActive)
        {
            HpRest.Start(now);
        }
        else if (_state.Position != PlayerPosition.Resting && HpRest.IsActive)
        {
            HpRest.Stop();
        }

        if (_state.Position == PlayerPosition.Meditating && !MpMedi.IsActive)
        {
            MpMedi.Start(now);
        }
        else if (_state.Position != PlayerPosition.Meditating && MpMedi.IsActive)
        {
            MpMedi.Stop();
        }
    }

    private bool IsInArtifactWindow(DateTimeOffset now)
        => _lastArtifactAt is { } at && now - at <= RegenConstants.ArtifactGraceWindow;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _state.PropertyChanged -= OnPlayerStateChanged;
    }
}

// One observed regen sample — payload of the tick-observed events.
// IntervalSinceLast is the wall-clock gap since the previous observed uptick
// of the same stream (HP or MA), or TimeSpan.Zero for the first sample. It
// carries the raw cadence a diagnostic can read a realm's real tick timing
// off of.
public readonly record struct RegenSample(
    DateTimeOffset Timestamp,
    int Delta,
    TimeSpan IntervalSinceLast,
    PlayerPosition Position);
