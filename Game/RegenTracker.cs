using System.ComponentModel;

namespace FujinTerm.Game;

/// <summary>
/// Observes HP and MA changes on <see cref="PlayerState"/> and folds
/// upward deltas into per-position running averages of the regen-tick
/// interval and per-tick amount. Bootstrap values come from
/// <see cref="RegenConstants"/> (seeded from syntax53's MMUD-Explorer
/// research); live observation refines them over time.
/// </summary>
/// <remarks>
/// <para>
/// Position-aware: when <see cref="PlayerState.Position"/> is Standing,
/// HP samples accumulate into <see cref="HpStanding"/>; resting samples
/// land in <see cref="HpResting"/>; etc. MA samples branch the same way.
/// Each position has its own running average so a rest tick doesn't
/// pollute the standing estimate.
/// </para>
/// <para>
/// Artifact filter (conservative — issue #8 tracks the upgrade path):
/// callers invoke <see cref="RecordArtifact"/> when the user performs a
/// heal-shaped action (cast / drink / quaff / etc.). Any upward HP / MA
/// change inside the <see cref="RegenConstants.ArtifactGraceWindow"/>
/// after that event is dropped as a sample — but the baseline is still
/// updated so the next genuine tick measures from the right anchor.
/// </para>
/// </remarks>
public sealed class RegenTracker : IDisposable
{
    private readonly PlayerState _state;
    private readonly Func<DateTimeOffset> _clock;
    private DateTimeOffset? _lastArtifactAt;
    private int _lastHp;
    private int _lastMa;
    private DateTimeOffset _lastHpSampleAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastMaSampleAt = DateTimeOffset.MinValue;
    private bool _hpBaselineSet;
    private bool _maBaselineSet;
    private bool _disposed;

    public RegenStat HpStanding   { get; } = new(RegenConstants.SeedStandingInterval);
    public RegenStat HpResting    { get; } = new(RegenConstants.SeedRestingInterval);
    public RegenStat HpMeditating { get; } = new(RegenConstants.SeedMeditatingInterval);

    public RegenStat MaStanding   { get; } = new(RegenConstants.SeedStandingInterval);
    public RegenStat MaResting    { get; } = new(RegenConstants.SeedRestingInterval);
    public RegenStat MaMeditating { get; } = new(RegenConstants.SeedMeditatingInterval);

    /// <summary>Fired after an observed HP increase passes the artifact filter.</summary>
    public event Action<RegenSample>? HpTickObserved;

    /// <summary>Fired after an observed MA increase passes the artifact filter.</summary>
    public event Action<RegenSample>? MaTickObserved;

    /// <summary>
    /// Construct with the <paramref name="state"/> to watch and an
    /// optional <paramref name="clock"/> for test-controllable timestamps.
    /// Production code omits the clock — defaults to <see cref="DateTimeOffset.Now"/>.
    /// </summary>
    public RegenTracker(PlayerState state, Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _state.PropertyChanged += OnPlayerStateChanged;
    }

    /// <summary>
    /// Mark the current moment as inside the artifact grace window — any
    /// HP / MA increase observed in the next
    /// <see cref="RegenConstants.ArtifactGraceWindow"/> won't be folded
    /// into the running averages. Called by
    /// <c>MainWindowViewModel.SendUserText</c> when it spots a heal-shaped
    /// command verb in the user's typed input.
    /// </summary>
    public void RecordArtifact()
    {
        _lastArtifactAt = _clock();
    }

    /// <summary>Wipe every running average back to seed + zero samples.</summary>
    public void ResetAll()
    {
        HpStanding.Reset();
        HpResting.Reset();
        HpMeditating.Reset();
        MaStanding.Reset();
        MaResting.Reset();
        MaMeditating.Reset();
        _hpBaselineSet = false;
        _maBaselineSet = false;
    }

    private void OnPlayerStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerState.Hp):  ConsiderHp();  break;
            case nameof(PlayerState.Ma):  ConsiderMa();  break;
        }
    }

    private void ConsiderHp()
    {
        int current = _state.Hp;
        DateTimeOffset now = _clock();

        if (!_hpBaselineSet)
        {
            _lastHp = current;
            _lastHpSampleAt = now;
            _hpBaselineSet = true;
            return;
        }

        int delta = current - _lastHp;
        // Always update the baseline so the next sample measures from the
        // freshest value.
        TimeSpan interval = now - _lastHpSampleAt;
        _lastHp = current;
        _lastHpSampleAt = now;

        if (delta <= 0) return;                              // downward = damage, not a regen tick.
        if (IsInArtifactWindow(now)) return;                 // heal / potion / etc. recently — drop.

        RegenStat target = HpStatFor(_state.Position);
        target.AddSample(interval, delta);
        HpTickObserved?.Invoke(new RegenSample(now, delta, interval, _state.Position));
    }

    private void ConsiderMa()
    {
        int current = _state.Ma;
        DateTimeOffset now = _clock();

        if (!_maBaselineSet)
        {
            _lastMa = current;
            _lastMaSampleAt = now;
            _maBaselineSet = true;
            return;
        }

        int delta = current - _lastMa;
        TimeSpan interval = now - _lastMaSampleAt;
        _lastMa = current;
        _lastMaSampleAt = now;

        if (delta <= 0) return;
        if (IsInArtifactWindow(now)) return;

        RegenStat target = MaStatFor(_state.Position);
        target.AddSample(interval, delta);
        MaTickObserved?.Invoke(new RegenSample(now, delta, interval, _state.Position));
    }

    private bool IsInArtifactWindow(DateTimeOffset now)
        => _lastArtifactAt is { } at && now - at <= RegenConstants.ArtifactGraceWindow;

    private RegenStat HpStatFor(PlayerPosition position) => position switch
    {
        PlayerPosition.Resting    => HpResting,
        PlayerPosition.Meditating => HpMeditating,
        _                         => HpStanding,
    };

    private RegenStat MaStatFor(PlayerPosition position) => position switch
    {
        PlayerPosition.Resting    => MaResting,
        PlayerPosition.Meditating => MaMeditating,
        _                         => MaStanding,
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _state.PropertyChanged -= OnPlayerStateChanged;
    }
}

/// <summary>One observed regen sample — payload of the tick-observed events.</summary>
public readonly record struct RegenSample(
    DateTimeOffset Timestamp,
    int Delta,
    TimeSpan IntervalSinceLast,
    PlayerPosition Position);
