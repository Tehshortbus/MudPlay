using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game.Cash;
using FujinTerm.Game.Combat;
using FujinTerm.Services;

namespace FujinTerm.ViewModels;

// Modeless Session Stats window VM. A pure projection over the three session
// trackers — CombatSessionTracker (Player Statistics), TimeAnalysisTracker
// (Time Analysis), and SessionActivityTracker (Session Statistics +
// kills/hour sparkline). It snapshots each on their Changed signal and
// exposes the figures for binding; the trackers own all the state and the
// session-reset boundary, so this VM never mutates game data.
//
// Combat Changed can fire many times a round, so refreshes are coalesced
// onto a single dispatcher tick rather than re-snapshotting per event. The
// snapshots are held as record-struct properties and the window binds their
// fields directly (with StringFormat for plain numbers / percentages);
// durations and damage ranges get formatted getters here since StringFormat
// can't express "hours past 24" or a min–max pair. A 1-second
// DispatcherTimer also re-snapshots on the wall clock so the durations and
// per-hour rates tick up live while the window is open, instead of only
// advancing when a tracker input changes.
public sealed partial class SessionStatsViewModel : ObservableObject, IDisposable
{
    // Bucket count for the kills/hour sparkline across the rolling window.
    private const int SparklineBuckets = 30;

    private readonly CombatSessionTracker _combatTracker;
    private readonly TimeAnalysisTracker _timeTracker;
    private readonly SessionActivityTracker _activityTracker;
    private readonly TransactionHistoryTracker _transactionTracker;
    private readonly SessionStatsLayoutStore _layoutStore;

    // Opens the Transaction history window. Routed back to
    // MainWindowViewModel so the window follows the modeless toggle-window
    // contract (re-press closes) rather than being spawned here.
    private readonly Action _openTransactionHistory;

    // Drives the live wall-clock ticking of durations / rates: the
    // time-derived figures advance with real time even when no tracker input
    // fires, so the user sees the session clock move.
    private readonly DispatcherTimer _liveTick;

    private bool _refreshScheduled;
    private bool _disposed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HitRangeText), nameof(CritRangeText), nameof(BackstabRangeText),
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
    private SessionActivityStats _activity;

    // Kills/hour series feeding the kills sparkline; reassigned each refresh.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KillsPeakText), nameof(KillsFloorText))]
    private IReadOnlyList<double> _killsPerHour = Array.Empty<double>();

    // Experience/hour series feeding the exp sparkline; reassigned each refresh.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpPeakText), nameof(ExpFloorText))]
    private IReadOnlyList<double> _experiencePerHour = Array.Empty<double>();

    // Per-panel visibility toggles — each of the five panels (the two rate
    // graphs and the three stat sections) can be shown or hidden via the
    // window's context menu. Each change is written through to the
    // per-character layout via PersistLayout; _loadingLayout gates the write
    // so applying a saved layout doesn't echo straight back to disk.
    [ObservableProperty]
    private bool _isKillsGraphVisible = true;

    [ObservableProperty]
    private bool _isExpGraphVisible = true;

    [ObservableProperty]
    private bool _isPlayerStatsVisible = true;

    [ObservableProperty]
    private bool _isTimeAnalysisVisible = true;

    [ObservableProperty]
    private bool _isSessionStatsVisible = true;

    // Panel ids in their resolved top-to-bottom order — the window reads this
    // on open to reorder its panel host, and pushes drag-reorders back via
    // SaveOrder.
    private List<string> _panelOrder = new();

    // Suppresses PersistLayout while LoadLayout seeds the visibility toggles
    // from a saved layout, so hydration doesn't immediately write the same
    // values back.
    private bool _loadingLayout;

    public SessionStatsViewModel(
        CombatSessionTracker combat,
        TimeAnalysisTracker time,
        SessionActivityTracker activity,
        TransactionHistoryTracker transactions,
        SessionStatsLayoutStore layout,
        Action openTransactionHistory)
    {
        ArgumentNullException.ThrowIfNull(combat);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(transactions);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(openTransactionHistory);
        _combatTracker = combat;
        _timeTracker = time;
        _activityTracker = activity;
        _transactionTracker = transactions;
        _layoutStore = layout;
        _openTransactionHistory = openTransactionHistory;

        LoadLayout();

        _combatTracker.Changed += OnChanged;
        _timeTracker.Changed += OnChanged;
        _activityTracker.Changed += OnChanged;

        _liveTick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _liveTick.Tick += (_, _) => Refresh();
        _liveTick.Start();

        Refresh();
    }

    // ----- Panel layout (order + visibility) ---------------------------

    // The resolved panel order the window applies on open.
    public IReadOnlyList<string> PanelOrder => _panelOrder;

    // Hydrate the order + visibility toggles from the per-character layout
    // store. Guarded so the toggle assignments don't write straight back.
    private void LoadLayout()
    {
        _loadingLayout = true;
        IReadOnlyList<(string Id, bool Visible)> resolved = _layoutStore.Resolve();
        _panelOrder = resolved.Select(p => p.Id).ToList();
        foreach ((string id, bool visible) in resolved)
            SetVisible(id, visible);
        _loadingLayout = false;
    }

    // Push a new panel order (from a drag-reorder) through to the store,
    // keeping the current hidden set.
    public void SaveOrder(IEnumerable<string> ids)
    {
        _panelOrder = ids.ToList();
        PersistLayout();
    }

    // Snapshot the live order + hidden set into the per-character store.
    private void PersistLayout()
    {
        if (_loadingLayout) return;
        List<string> hidden = new();
        if (!IsKillsGraphVisible)   hidden.Add("KillsGraph");
        if (!IsExpGraphVisible)     hidden.Add("ExpGraph");
        if (!IsPlayerStatsVisible)  hidden.Add("PlayerStatistics");
        if (!IsTimeAnalysisVisible) hidden.Add("TimeAnalysis");
        if (!IsSessionStatsVisible) hidden.Add("SessionStatistics");
        _layoutStore.Update(_panelOrder, hidden);
    }

    private void SetVisible(string id, bool visible)
    {
        switch (id)
        {
            case "KillsGraph":        IsKillsGraphVisible = visible; break;
            case "ExpGraph":          IsExpGraphVisible = visible; break;
            case "PlayerStatistics":  IsPlayerStatsVisible = visible; break;
            case "TimeAnalysis":      IsTimeAnalysisVisible = visible; break;
            case "SessionStatistics": IsSessionStatsVisible = visible; break;
        }
    }

    partial void OnIsKillsGraphVisibleChanged(bool value) => PersistLayout();
    partial void OnIsExpGraphVisibleChanged(bool value) => PersistLayout();
    partial void OnIsPlayerStatsVisibleChanged(bool value) => PersistLayout();
    partial void OnIsTimeAnalysisVisibleChanged(bool value) => PersistLayout();
    partial void OnIsSessionStatsVisibleChanged(bool value) => PersistLayout();

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

    // ----- Player Statistics (damage ranges) ---------------------------

    public string HitRangeText      => Range(Combat.HitMinDamage, Combat.HitMaxDamage);
    public string CritRangeText     => Range(Combat.CritMinDamage, Combat.CritMaxDamage);
    public string BackstabRangeText => Range(Combat.BackstabMinDamage, Combat.BackstabMaxDamage);
    public string RoundRangeText    => Range(Combat.RoundMinDamage, Combat.RoundMaxDamage);
    public string ProcRangeText     => Range(Combat.ProcMinDamage, Combat.ProcMaxDamage);
    public string SpellRangeText    => Range(Combat.SpellMinDamage, Combat.SpellMaxDamage);

    // Drives the proc row's visibility — hidden until a weapon procs.
    public bool HasProcs => Combat.ProcHits > 0;

    // Drives the spell row's visibility — hidden until a configured attack
    // spell lands.
    public bool HasSpells => Combat.SpellHits > 0;

    // ----- Rate-graph scales -------------------------------------------
    // The sparklines normalise each series to its own min–max, so the plot is
    // shapely but unitless on its own. These label the y-axis — peak at the top
    // edge, floor at the bottom — so a glance reads off the value the line maps
    // to. Exp rates run large, so their labels abbreviate (k / M).

    public string KillsPeakText  => RateLabel(Peak(KillsPerHour), compact: false);
    public string KillsFloorText => RateLabel(Floor(KillsPerHour), compact: false);
    public string ExpPeakText    => RateLabel(Peak(ExperiencePerHour), compact: true);
    public string ExpFloorText   => RateLabel(Floor(ExperiencePerHour), compact: true);

    private static double Peak(IReadOnlyList<double> s)  => s.Count > 0 ? s.Max() : 0;
    private static double Floor(IReadOnlyList<double> s) => s.Count > 0 ? s.Min() : 0;

    private static string RateLabel(double v, bool compact)
    {
        if (v <= 0) return "0";
        if (!compact) return v.ToString("F0");
        return v switch
        {
            >= 1_000_000 => $"{v / 1_000_000d:0.#}M",
            >= 1_000     => $"{v / 1_000d:0.#}k",
            _            => v.ToString("F0"),
        };
    }

    // ----- Commands ----------------------------------------------------

    // Manual "Reset session" — wipes every session tracker's counters and
    // restarts their clocks, and clears the transaction ledger. The resets
    // raise Changed, which refreshes the bound figures.
    [RelayCommand]
    private void Reset()
    {
        _combatTracker.Reset();
        _timeTracker.Reset();
        _activityTracker.Reset();
        _transactionTracker.Reset();
    }

    // "Transaction history" button — opens the modeless ledger window (bank
    // deposits + stash-room hides recorded this session).
    [RelayCommand]
    private void OpenTransactionHistory() => _openTransactionHistory();

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
        ExperiencePerHour = _activityTracker.ExperiencePerHourSeries(SparklineBuckets);
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
