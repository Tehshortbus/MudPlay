using System.ComponentModel;

namespace FujinTerm.Game;

/// <summary>
/// Watches <see cref="PlayerState"/> and runs four independent regen
/// cycles whose anchors and intervals reflect what the MajorMUD server
/// actually does (per syntax53/MMUD-Explorer's <c>modExpPerHour.bas</c>):
/// </summary>
/// <list type="table">
///   <listheader><term>Cycle</term><description>Behaviour</description></listheader>
///   <item>
///     <term><see cref="HpNatural"/></term>
///     <description>30 s. Always running once anchored on the first
///     observed HP uptick. Per-tick amount = <c>HPRegen / 3</c>.</description>
///   </item>
///   <item>
///     <term><see cref="HpRest"/></term>
///     <description>20 s. Starts the moment the user enters
///     <see cref="PlayerPosition.Resting"/>, stops when they leave.
///     Anchor is independent of the natural cycle. Per-tick amount =
///     <c>HPRegen</c>.</description>
///   </item>
///   <item>
///     <term><see cref="MpNatural"/></term>
///     <description>30 s. Always running once anchored. Per-tick amount
///     = <c>MPRegen</c>.</description>
///   </item>
///   <item>
///     <term><see cref="MpMedi"/></term>
///     <description>10 s. Starts on entering
///     <see cref="PlayerPosition.Meditating"/>, stops on leaving.
///     Per-tick amount = <c>MeditateRate</c>.</description>
///   </item>
/// </list>
/// <remarks>
/// <para>
/// The natural cycle and the bonus cycle can be (and usually are)
/// desynchronised — the natural anchors at first observation; the
/// bonus anchors when the user types <c>rest</c> / <c>meditate</c>.
/// The status bar shows them as parallel countdowns.
/// </para>
/// <para>
/// The two natural cycles (HP + MA) fire on the same server pulse,
/// however — empirically verified. Any observation that anchors one
/// mirrors the anchor onto the other so a max-HP / max-MA character
/// still has a live countdown driven by whichever stream is moving.
/// </para>
/// </remarks>
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

    public RegenCycle HpNatural { get; } = new("HP natural", RegenConstants.SeedStandingInterval);
    public RegenCycle HpRest    { get; } = new("HP rest",    RegenConstants.SeedRestingInterval);
    public RegenCycle MpNatural { get; } = new("MP natural", RegenConstants.SeedStandingInterval);
    public RegenCycle MpMedi    { get; } = new("MP medi",    RegenConstants.SeedMeditatingInterval);

    /// <summary>Fired after an observed HP uptick that passes the artifact filter.</summary>
    public event Action<RegenSample>? HpTickObserved;

    /// <summary>Fired after an observed MA uptick that passes the artifact filter.</summary>
    public event Action<RegenSample>? MaTickObserved;

    public RegenTracker(PlayerState state, Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _state.PropertyChanged += OnPlayerStateChanged;
    }

    /// <summary>Time-to-next HP natural tick, or <c>null</c> before first observation.</summary>
    public TimeSpan? GetTimeToNextHpNaturalTick() => HpNatural.GetTimeToNext(_clock());

    /// <summary>Time-to-next HP rest tick, or <c>null</c> when not resting.</summary>
    public TimeSpan? GetTimeToNextHpRestTick() => HpRest.GetTimeToNext(_clock());

    /// <summary>Time-to-next MP natural tick, or <c>null</c> before first observation.</summary>
    public TimeSpan? GetTimeToNextMpNaturalTick() => MpNatural.GetTimeToNext(_clock());

    /// <summary>Time-to-next MP meditate tick, or <c>null</c> when not meditating.</summary>
    public TimeSpan? GetTimeToNextMpMediTick() => MpMedi.GetTimeToNext(_clock());

    /// <summary>Mark the moment as an artifact (heal / drink / etc.) so subsequent up-deltas drop.</summary>
    public void RecordArtifact() => _lastArtifactAt = _clock();

    /// <summary>Reset every cycle's amount stat + stop bonus cycles. Natural cycles keep their anchor.</summary>
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

    /// <summary>
    /// If <paramref name="cycle"/> is active and the now-instant is at
    /// or past its next-tick boundary (with a small grace), record the
    /// observation and return true.
    /// </summary>
    private static bool ClaimIfDue(RegenCycle cycle, DateTimeOffset now, double delta)
    {
        if (cycle.Anchor is not { } anchor) return false;
        TimeSpan elapsed = now - anchor;
        TimeSpan grace = TimeSpan.FromMilliseconds(750);
        if (elapsed + grace < cycle.Interval) return false;
        cycle.RecordObservation(now, delta);
        return true;
    }

    /// <summary>
    /// Start / stop the rest + medi bonus cycles on position transitions.
    /// Leaving the position before the cycle's interval elapses cancels the
    /// pending tick outright (no partial credit) — the server only fires
    /// the rest / medi tick if the player stayed in the position for the
    /// full interval. Re-entering re-anchors from the new transition.
    /// </summary>
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

/// <summary>
/// One observed regen sample — payload of the tick-observed events.
/// <paramref name="IntervalSinceLast"/> is the wall-clock gap since the
/// previous observed uptick of the <i>same</i> stream (HP or MA), or
/// <see cref="TimeSpan.Zero"/> for the first sample. It carries the raw
/// cadence a diagnostic can read a realm's real tick timing off of.
/// </summary>
public readonly record struct RegenSample(
    DateTimeOffset Timestamp,
    int Delta,
    TimeSpan IntervalSinceLast,
    PlayerPosition Position);
