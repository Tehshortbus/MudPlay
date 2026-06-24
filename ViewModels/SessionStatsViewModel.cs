using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game.Combat;

namespace FujinTerm.ViewModels;

/// <summary>
/// Modeless Session Stats window VM. A pure projection over the three Phase 11
/// trackers — <see cref="CombatSessionTracker"/> (Player Statistics),
/// <see cref="TimeAnalysisTracker"/> (Time Analysis), and
/// <see cref="SessionActivityTracker"/> (Session Statistics + kills/hour
/// sparkline). It snapshots each on their <c>Changed</c> signal and exposes the
/// figures for binding; the trackers own all the state and the session-reset
/// boundary, so this VM never mutates game data.
/// </summary>
/// <remarks>
/// Combat <c>Changed</c> can fire many times a round, so refreshes are coalesced
/// onto a single dispatcher tick rather than re-snapshotting per event. The
/// snapshots are held as record-struct properties and the window binds their
/// fields directly (with <c>StringFormat</c> for plain numbers / percentages);
/// durations and damage ranges get formatted getters here since
/// <c>StringFormat</c> can't express "hours past 24" or a min–max pair. A 1-second
/// <see cref="DispatcherTimer"/> also re-snapshots on the wall clock so the
/// durations and per-hour rates tick up live while the window is open, instead of
/// only advancing when a tracker input changes.
/// </remarks>
public sealed partial class SessionStatsViewModel : ObservableObject, IDisposable
{
    /// <summary>Bucket count for the kills/hour sparkline across the rolling window.</summary>
    private const int SparklineBuckets = 30;

    private readonly CombatSessionTracker _combatTracker;
    private readonly TimeAnalysisTracker _timeTracker;
    private readonly SessionActivityTracker _activityTracker;

    /// <summary>Drives the live wall-clock ticking of durations / rates: the
    /// time-derived figures advance with real time even when no tracker input
    /// fires, so the user sees the session clock move.</summary>
    private readonly DispatcherTimer _liveTick;

    private bool _refreshScheduled;
    private bool _disposed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PhysicalRangeText), nameof(BackstabRangeText),
        nameof(RoundRangeText), nameof(ProcRangeText), nameof(SpellRangeText),
        nameof(HasProcs), nameof(HasSpells))]
    private CombatSessionStats _combat;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationText), nameof(MovingText), nameof(AttackingText),
        nameof(RestingText), nameof(WaitingText), nameof(RestingHpText), nameof(RestingMaText),
        nameof(BlindedText), nameof(PoisonedText), nameof(DiseasedText), nameof(ConfusedText),
        nameof(HeldText))]
    private TimeAnalysisStats _time;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeOnlineText))]
    private SessionActivityStats _activity;

    /// <summary>Kills/hour series feeding the sparkline; reassigned each refresh.</summary>
    [ObservableProperty]
    private IReadOnlyList<double> _killsPerHour = Array.Empty<double>();

    public SessionStatsViewModel(
        CombatSessionTracker combat,
        TimeAnalysisTracker time,
        SessionActivityTracker activity)
    {
        ArgumentNullException.ThrowIfNull(combat);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(activity);
        _combatTracker = combat;
        _timeTracker = time;
        _activityTracker = activity;

        _combatTracker.Changed += OnChanged;
        _timeTracker.Changed += OnChanged;
        _activityTracker.Changed += OnChanged;

        _liveTick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _liveTick.Tick += (_, _) => Refresh();
        _liveTick.Start();

        Refresh();
    }

    // ----- Time Analysis (durations) -----------------------------------

    public string DurationText  => Fmt(Time.TimeOn);
    public string MovingText    => Fmt(Time.Moving);
    public string AttackingText => Fmt(Time.Attacking);
    public string RestingText   => Fmt(Time.Resting);
    public string WaitingText   => Fmt(Time.Waiting);
    public string RestingHpText => Fmt(Time.RestingHp);
    public string RestingMaText => Fmt(Time.RestingMa);
    public string BlindedText   => Fmt(Time.Blinded);
    public string PoisonedText  => Fmt(Time.Poisoned);
    public string DiseasedText  => Fmt(Time.Diseased);
    public string ConfusedText  => Fmt(Time.Confused);
    public string HeldText      => Fmt(Time.Held);

    /// <summary>Session online time, sourced from the activity tracker so the
    /// "Session Statistics" section stays self-consistent with the kills/hour and
    /// exp/hour rates derived from the same clock.</summary>
    public string TimeOnlineText => Fmt(Activity.TimeOnline);

    // ----- Player Statistics (damage ranges) ---------------------------

    public string PhysicalRangeText => Range(Combat.PhysicalMinDamage, Combat.PhysicalMaxDamage);
    public string BackstabRangeText => Range(Combat.BackstabMinDamage, Combat.BackstabMaxDamage);
    public string RoundRangeText    => Range(Combat.RoundMinDamage, Combat.RoundMaxDamage);
    public string ProcRangeText     => Range(Combat.ProcMinDamage, Combat.ProcMaxDamage);
    public string SpellRangeText    => Range(Combat.SpellMinDamage, Combat.SpellMaxDamage);

    /// <summary>Drives the proc row's visibility — hidden until a weapon procs.</summary>
    public bool HasProcs => Combat.ProcHits > 0;

    /// <summary>Drives the spell row's visibility — hidden until a configured
    /// attack spell lands.</summary>
    public bool HasSpells => Combat.SpellHits > 0;

    // ----- Commands ----------------------------------------------------

    /// <summary>Manual "Reset session" — wipes every Phase 11 tracker's counters
    /// and restarts their clocks. The resets raise <c>Changed</c>, which refreshes
    /// the bound figures.</summary>
    [RelayCommand]
    private void Reset()
    {
        _combatTracker.Reset();
        _timeTracker.Reset();
        _activityTracker.Reset();
    }

    // ----- Refresh plumbing --------------------------------------------

    private void OnChanged()
    {
        if (_refreshScheduled) return;
        _refreshScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshScheduled = false;
            if (!_disposed) Refresh();
        });
    }

    private void Refresh()
    {
        Combat = _combatTracker.Snapshot();
        Time = _timeTracker.Snapshot();
        Activity = _activityTracker.Snapshot();
        KillsPerHour = _activityTracker.KillsPerHourSeries(SparklineBuckets);
    }

    private static string Fmt(TimeSpan t) =>
        $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}";

    private static string Range(int min, int max) =>
        max <= 0 ? "—" : $"{min}–{max}";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _liveTick.Stop();
        _combatTracker.Changed -= OnChanged;
        _timeTracker.Changed -= OnChanged;
        _activityTracker.Changed -= OnChanged;
    }
}
