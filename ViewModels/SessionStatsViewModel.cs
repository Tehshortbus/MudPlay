using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game;
using MudPlay.Game.Calculators;
using MudPlay.Game.Cash;
using MudPlay.Game.Combat;
using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.ViewModels;

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

    // How many loop steps the HP/MA History graph shows at once; the slider pans
    // this window across a longer loop.
    private const int StepViewWindow = 15;

    // Percentage points of headroom below the lowest recorded value for the HP/MA
    // graph's axis floor — so the plot spreads over the range that matters instead
    // of wasting the bottom half on values you never reach.
    private const double AxisFloorHeadroom = 15;

    private readonly CombatSessionTracker _combatTracker;
    private readonly TimeAnalysisTracker _timeTracker;
    private readonly SessionActivityTracker _activityTracker;
    private readonly Func<(TimeToLevelEstimator.Result Estimate, TimeSpan? Remaining)> _selfTimeToLevel;
    private readonly HpMaHistoryTracker _hpMaTracker;
    private readonly SessionStatsLayoutStore _layoutStore;

    // Resolves the per-BBS runic word for the currency denomination labels.
    private readonly CurrencyNaming _naming;

    // Live progression + game data for the time-to-level readout: PlayerStats
    // supplies level / exp / class / race, GameDataCache supplies the exp chart
    // and active realm. Read-only here — the trackers own all session state.
    private readonly PlayerStats _stats;
    private readonly GameDataCache _gameData;

    // Opens the Transaction history window. Routed back to
    // MainWindowViewModel so the window follows the modeless toggle-window
    // contract (re-press closes) rather than being spawned here.
    private readonly Action _openTransactionHistory;

    // Opens the Players Seen window — routed back to MainWindowViewModel for the
    // same modeless toggle-window reason as the transaction opener above.
    private readonly Action _openPlayersSeen;

    // Loop-lap readouts read straight off the runner each tick (it already tracks
    // durations, count, and the circle-start room). The graph resolves that start
    // room's key to a display name.
    private readonly LoopRunner _runner;
    private readonly RoomGraphManager _graph;

    // Drives the live wall-clock ticking of durations / rates: the
    // time-derived figures advance with real time even when no tracker input
    // fires, so the user sees the session clock move.
    private readonly DispatcherTimer _liveTick;

    private bool _refreshScheduled;
    // The runner's lap count when Time Analysis was last reset (see LapCount).
    private int _lapBase;
    private bool _disposed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HitRangeText), nameof(CritRangeText), nameof(BackstabRangeText),
        nameof(RoundRangeText), nameof(ProcRangeText), nameof(SpellRangeText),
        nameof(HitTakenRangeText), nameof(RoundsToKillRangeText), nameof(RoundsToKillAvgText),
        nameof(HasProcs), nameof(HasSpells))]
    private CombatSessionStats _combat;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationText), nameof(MovingText), nameof(AttackingText),
        nameof(RestingText), nameof(WaitingText), nameof(WaitingTip), nameof(RestingHpText), nameof(RestingMaText),
        nameof(BlindedText), nameof(PoisonedText), nameof(DiseasedText), nameof(ConfusedText),
        nameof(HeldText))]
    private TimeAnalysisStats _time;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrencyCollectedText), nameof(CurrencyCollectedTip),
        nameof(CurrencyPerHourText), nameof(CurrencyStashedText), nameof(CurrencyStashedTip),
        nameof(CurrencyDepositedText), nameof(CurrencyDepositedTip),
        nameof(SneakText), nameof(SneakTip), nameof(DisarmText), nameof(DisarmTip), nameof(WalkText), nameof(WalkTip),
        nameof(KillsRateText), nameof(ExpRateText))]
    private SessionActivityStats _activity;

    // The shared TNL clock, read once per refresh so the countdown under the exp
    // graph and the Exp needed / Will level in rows all show the same reading.
    private (TimeToLevelEstimator.Result Estimate, TimeSpan? Remaining) _tnl;

    // Kills/hour series feeding the kills sparkline; reassigned each refresh.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KillsPeakText), nameof(KillsFloorText))]
    private IReadOnlyList<double> _killsPerHour = Array.Empty<double>();

    // Experience/hour series feeding the exp sparkline; reassigned each refresh.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpPeakText), nameof(ExpFloorText))]
    private IReadOnlyList<double> _experiencePerHour = Array.Empty<double>();

    // Per-loop-step HP/MA min/max (percent of max), indexed by step position;
    // reassigned each refresh. HP and MA each draw a per-step high-low bar on a
    // shared 0–100% axis. HasManaHistory is false for a no-mana class, hiding the
    // mana bars + legend. The step count (HpLow.Count) drives the slider bounds.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepViewMax), nameof(IsStepSliderVisible), nameof(StepRangeText),
        nameof(AxisMin), nameof(AxisMinText), nameof(AxisQ1Text), nameof(AxisMidText), nameof(AxisQ3Text), nameof(CenterStepText), nameof(WindowOffset), nameof(CursorIndex))]
    private IReadOnlyList<double> _hpLow = Array.Empty<double>();
    [ObservableProperty] private IReadOnlyList<double> _hpHigh = Array.Empty<double>();
    [ObservableProperty] private IReadOnlyList<double> _hpAvg = Array.Empty<double>();
    [ObservableProperty] private IReadOnlyList<double> _maLow = Array.Empty<double>();
    [ObservableProperty] private IReadOnlyList<double> _maHigh = Array.Empty<double>();
    [ObservableProperty] private IReadOnlyList<double> _maAvg = Array.Empty<double>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AxisMin), nameof(AxisMinText), nameof(AxisQ1Text), nameof(AxisMidText), nameof(AxisQ3Text))]
    private bool _hasManaHistory;

    // Lowest HP / MA percent seen anywhere on the loop, shown in each series'
    // legend label and feeding the adaptive axis floor. 100 until the first on-loop
    // sample lands.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HpLegendText), nameof(AxisMin), nameof(AxisMinText), nameof(AxisQ1Text), nameof(AxisMidText), nameof(AxisQ3Text))]
    private double _lowestHpPercent = 100;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaLegendText), nameof(AxisMin), nameof(AxisMinText), nameof(AxisQ1Text), nameof(AxisMidText), nameof(AxisQ3Text))]
    private double _lowestMaPercent = 100;

    // The loop step the slider is anchored on (0-based). The slider spans every
    // step (0 … N-1), so the cursor reaches any step; the visible window follows it
    // (WindowOffset), centring where it can and clamping at the head / tail.
    // Clamped to StepViewMax each refresh.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepRangeText), nameof(CenterStepText),
        nameof(WindowOffset), nameof(CursorIndex))]
    private double _focusStep;

    // True while the user is holding / dragging the pan slider — drives the graph's
    // vertical scrub cursor so they can see exactly which step they're centred on.
    [ObservableProperty] private bool _isScrubbing;

    // Per-panel visibility toggles — each of the six panels (the three graphs
    // and the three stat sections) can be shown or hidden via the
    // window's context menu. Each change is written through to the
    // per-character layout via PersistLayout; _loadingLayout gates the write
    // so applying a saved layout doesn't echo straight back to disk.
    [ObservableProperty]
    private bool _isKillsGraphVisible = true;

    [ObservableProperty]
    private bool _isExpGraphVisible = true;

    [ObservableProperty]
    private bool _isHpMaGraphVisible = true;

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
        HpMaHistoryTracker hpMaHistory,
        LoopRunner runner,
        RoomGraphManager graph,
        SessionStatsLayoutStore layout,
        PlayerStats stats,
        GameDataCache gameData,
        CurrencyNaming naming,
        Func<(TimeToLevelEstimator.Result Estimate, TimeSpan? Remaining)> selfTimeToLevel,
        Action openTransactionHistory,
        Action openPlayersSeen)
    {
        ArgumentNullException.ThrowIfNull(combat);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(hpMaHistory);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(naming);
        ArgumentNullException.ThrowIfNull(selfTimeToLevel);
        ArgumentNullException.ThrowIfNull(openTransactionHistory);
        ArgumentNullException.ThrowIfNull(openPlayersSeen);
        _combatTracker = combat;
        _timeTracker = time;
        _activityTracker = activity;
        _selfTimeToLevel = selfTimeToLevel;
        _hpMaTracker = hpMaHistory;
        _runner = runner;
        _graph = graph;
        _layoutStore = layout;
        _stats = stats;
        _gameData = gameData;
        _naming = naming;
        _openTransactionHistory = openTransactionHistory;
        _openPlayersSeen = openPlayersSeen;

        LoadLayout();

        _combatTracker.Changed += OnChanged;
        _timeTracker.Changed += OnChanged;
        _activityTracker.Changed += OnChanged;
        _hpMaTracker.Changed += OnChanged;

        _liveTick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _liveTick.Tick += (_, _) => Refresh();
        _liveTick.Start();

        Refresh();
    }

    // ----- Panel layout (order + visibility) ---------------------------

    // The resolved panel order the window applies on open.
    public IReadOnlyList<string> PanelOrder => _panelOrder;

    // One tall column or two side by side (user, 2026-09-30). In two, the first
    // SplitAt panels of PanelOrder fill the left column; it starts at half.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOneColumn), nameof(IsTwoColumns))]
    private int _columns = 1;
    public bool IsOneColumn => Columns == 1;
    public bool IsTwoColumns => Columns == 2;
    public int SplitAt { get; private set; }

    [RelayCommand]
    private void SetColumns(string count) => Columns = count == "2" ? 2 : 1;
    partial void OnColumnsChanged(int value) => PersistLayout();

    // Hydrate the order + visibility toggles from the per-character layout
    // store. Guarded so the toggle assignments don't write straight back.
    private void LoadLayout()
    {
        _loadingLayout = true;
        IReadOnlyList<(string Id, bool Visible)> resolved = _layoutStore.Resolve();
        _panelOrder = resolved.Select(p => p.Id).ToList();
        foreach ((string id, bool visible) in resolved)
            SetVisible(id, visible);
        Columns = _layoutStore.Columns;
        SplitAt = Math.Clamp(_layoutStore.SplitAt ?? (_panelOrder.Count + 1) / 2, 0, _panelOrder.Count);
        IReadOnlyCollection<string> expanded = _layoutStore.Expanded;
        IsKillsGraphExpanded = expanded.Contains("KillsGraph");
        IsExpGraphExpanded = expanded.Contains("ExpGraph");
        IsHpMaGraphExpanded = expanded.Contains("HpMaGraph");
        IsPlayerStatsExpanded = expanded.Contains("PlayerStatistics");
        IsTimeAnalysisExpanded = expanded.Contains("TimeAnalysis");
        IsSessionStatsExpanded = expanded.Contains("SessionStatistics");
        _loadingLayout = false;
    }

    // Push a new panel order (from a drag-reorder) through to the store,
    // keeping the current hidden set.
    public void SaveOrder(IEnumerable<string> ids, int splitAt)
    {
        _panelOrder = ids.ToList();
        SplitAt = Math.Clamp(splitAt, 0, _panelOrder.Count);
        PersistLayout();
    }

    // Snapshot the live order + hidden set into the per-character store.
    private void PersistLayout()
    {
        if (_loadingLayout) return;
        List<string> hidden = new();
        if (!IsKillsGraphVisible)   hidden.Add("KillsGraph");
        if (!IsExpGraphVisible)     hidden.Add("ExpGraph");
        if (!IsHpMaGraphVisible)    hidden.Add("HpMaGraph");
        if (!IsPlayerStatsVisible)  hidden.Add("PlayerStatistics");
        if (!IsTimeAnalysisVisible) hidden.Add("TimeAnalysis");
        if (!IsSessionStatsVisible) hidden.Add("SessionStatistics");
        List<string> expanded = new();
        if (IsKillsGraphExpanded)   expanded.Add("KillsGraph");
        if (IsExpGraphExpanded)     expanded.Add("ExpGraph");
        if (IsHpMaGraphExpanded)    expanded.Add("HpMaGraph");
        if (IsPlayerStatsExpanded)  expanded.Add("PlayerStatistics");
        if (IsTimeAnalysisExpanded) expanded.Add("TimeAnalysis");
        if (IsSessionStatsExpanded) expanded.Add("SessionStatistics");
        _layoutStore.Update(_panelOrder, hidden, expanded, Columns, SplitAt);
    }

    private void SetVisible(string id, bool visible)
    {
        switch (id)
        {
            case "KillsGraph":        IsKillsGraphVisible = visible; break;
            case "ExpGraph":          IsExpGraphVisible = visible; break;
            case "HpMaGraph":         IsHpMaGraphVisible = visible; break;
            case "PlayerStatistics":  IsPlayerStatsVisible = visible; break;
            case "TimeAnalysis":      IsTimeAnalysisVisible = visible; break;
            case "SessionStatistics": IsSessionStatsVisible = visible; break;
        }
    }

    // Every panel starts collapsed; the ones the user opens are remembered per
    // character with the order and hidden set (user, 2026-09-30).
    [ObservableProperty] private bool _isKillsGraphExpanded;
    [ObservableProperty] private bool _isExpGraphExpanded;
    [ObservableProperty] private bool _isHpMaGraphExpanded;
    [ObservableProperty] private bool _isPlayerStatsExpanded;
    [ObservableProperty] private bool _isTimeAnalysisExpanded;
    [ObservableProperty] private bool _isSessionStatsExpanded;
    partial void OnIsKillsGraphExpandedChanged(bool value) => PersistLayout();
    partial void OnIsExpGraphExpandedChanged(bool value) => PersistLayout();
    partial void OnIsHpMaGraphExpandedChanged(bool value) => PersistLayout();
    partial void OnIsPlayerStatsExpandedChanged(bool value) => PersistLayout();
    partial void OnIsTimeAnalysisExpandedChanged(bool value) => PersistLayout();
    partial void OnIsSessionStatsExpandedChanged(bool value) => PersistLayout();

    partial void OnIsKillsGraphVisibleChanged(bool value) => PersistLayout();
    partial void OnIsExpGraphVisibleChanged(bool value) => PersistLayout();
    partial void OnIsHpMaGraphVisibleChanged(bool value) => PersistLayout();
    partial void OnIsPlayerStatsVisibleChanged(bool value) => PersistLayout();
    partial void OnIsTimeAnalysisVisibleChanged(bool value) => PersistLayout();
    partial void OnIsSessionStatsVisibleChanged(bool value) => PersistLayout();

    // ----- Time Analysis (durations) -----------------------------------

    public string DurationText  => Fmt(Time.TimeOn);
    public string MovingText    => Fmt(Time.Moving);
    public string AttackingText => Fmt(Time.Attacking);
    public string RestingText   => Fmt(Time.Resting);
    public string WaitingText   => Fmt(Time.Waiting);

    // What the Waiting total is made of: each thing that held the character still
    // and for how long, longest first, then the total they add up to.
    public string WaitingTip
    {
        get
        {
            if (Time.WaitingBy.Count == 0) return "Time standing still, not moving, fighting or resting. Nothing yet.";
            StringBuilder sb = new("Time standing still, by what held it:\n");
            foreach (WaitCauseTime w in Time.WaitingBy) sb.Append('\n').Append(Fmt(w.Time)).Append("  ").Append(w.Cause);
            return sb.Append("\n\n").Append(Fmt(Time.Waiting)).Append("  total").ToString();
        }
    }
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
    public string HitTakenRangeText => Range(Combat.HitTakenMinDamage, Combat.HitTakenMaxDamage);
    public string RoundsToKillRangeText => Range(Combat.RoundsToKillMin, Combat.RoundsToKillMax);
    // One decimal: a session of one- and two-round kills reads 1.5, not 2.
    public string RoundsToKillAvgText => Combat.TimedKills == 0 ? string.Empty : Combat.AvgRoundsToKill.ToString("F1");

    // Drives the proc row's visibility — hidden until one of our procs fires.
    public bool HasProcs => Combat.ProcHits > 0;

    // Drives the spell rows' visibility — hidden until one of our spells lands or
    // is resisted.
    public bool HasSpells => Combat.Spells.Count > 0;

    // ----- Session Statistics (currency) -------------------------------
    // The copper totals/rate read as coin denominations rather than raw
    // comma-grouped copper, flipping up to the largest denomination that fits
    // (1000 copper/hr -> "10 gold/hr"). The exact itemised wealth line rides
    // along as a tooltip for the total/stashed figures.

    public string CurrencyCollectedText => CurrencyFormat.Denominate(Activity.CurrencyCollected, _naming.RunicName);
    public string CurrencyCollectedTip  => CurrencyFormat.Full(Activity.CurrencyCollected, _naming.RunicName);
    public string CurrencyPerHourText   => CurrencyFormat.Denominate(Activity.CurrencyPerHour, _naming.RunicName);
    public string CurrencyStashedText   => CurrencyFormat.Denominate(Activity.CurrencyStashed, _naming.RunicName);
    public string CurrencyStashedTip    => CurrencyFormat.Full(Activity.CurrencyStashed, _naming.RunicName);
    public string CurrencyDepositedText => CurrencyFormat.Denominate(Activity.CurrencyDeposited, _naming.RunicName);
    public string CurrencyDepositedTip  => CurrencyFormat.Full(Activity.CurrencyDeposited, _naming.RunicName);

    // ----- Session Statistics (sneak + walk) ---------------------------

    public string SneakText => Activity.SneakPercent is { } p ? RateText.Percent(p) : "—";
    public string SneakTip  => $"{Activity.SneakHeld:N0} of {Activity.SneakEntries:N0} rooms entered while sneaking kept the sneak.";
    public string DisarmText => Activity.DisarmPercent is { } d
        ? $"{Activity.TrapsDisarmed:N0} · {RateText.Percent(d)}" : "—";
    public string DisarmTip  => $"{Activity.TrapsDisarmed:N0} of {Activity.DisarmAttempts:N0} disarm attempts disarmed the trap.";
    public string WalkText  => Activity.AverageStep is { } step ? $"{step.TotalSeconds:F2}s" : "—";
    public string WalkTip   => $"Average from a move going out to the new room showing, over {Activity.Steps:N0} walk / loop steps.";

    // ----- Rate-graph scales -------------------------------------------
    // The sparklines normalise each series to its own min–max, so the plot is
    // shapely but unitless on its own. These label the y-axis — peak at the top
    // edge, floor at the bottom — so a glance reads off the value the line maps
    // to. Exp rates run large, so their labels abbreviate (k / M).

    public string KillsPeakText  => RateLabel(Peak(KillsPerHour), compact: false);
    public string KillsFloorText => RateLabel(Floor(KillsPerHour), compact: false);
    public string ExpPeakText    => RateLabel(Peak(ExperiencePerHour), compact: true);
    public string ExpFloorText   => RateLabel(Floor(ExperiencePerHour), compact: true);

    // HP/MA-history legend labels — each names the series and its worst dip across
    // the loop's steps ("HP (low 28%)"), so the scariest moment reads without
    // eyeballing the bars. 100% until the first on-loop sample lands.
    public string HpLegendText => $"HP (low {RateText.Percent(LowestHpPercent)})";
    public string MaLegendText => $"MA (low {RateText.Percent(LowestMaPercent)})";

    // Slider spans every step (its max is the last step index), so the cursor can
    // anchor anywhere; only shown once the loop is longer than one window (a loop
    // that fits needs no panning).
    public double StepViewMax => Math.Max(0, HpLow.Count - 1);
    public bool IsStepSliderVisible => HpLow.Count > StepViewWindow;

    // The anchored step (0-based, clamped) the cursor + readout mark.
    public int CursorIndex
    {
        get
        {
            int n = HpLow.Count;
            return n == 0 ? 0 : Math.Clamp((int)Math.Round(FocusStep), 0, n - 1);
        }
    }

    // First visible step: the window follows the cursor — centred on it where it
    // can be, clamped so the head and tail steps stay reachable.
    public int WindowOffset
    {
        get
        {
            int n = HpLow.Count;
            if (n == 0) return 0;
            int window = Math.Min(StepViewWindow, n);
            return Math.Clamp(CursorIndex - window / 2, 0, n - window);
        }
    }

    // Adaptive vertical floor for the graph (top is fixed at 100%): AxisFloorHeadroom points below
    // the lowest value seen across the plotted series, clamped at 0. 0 before any
    // data lands. AxisMinText labels the bottom scale tick.
    public double AxisMin
    {
        get
        {
            if (HpLow.Count == 0) return 0;
            double lowest = HasManaHistory ? Math.Min(LowestHpPercent, LowestMaPercent) : LowestHpPercent;
            return Math.Max(0, lowest - AxisFloorHeadroom);
        }
    }

    public string AxisMinText => $"{AxisMin:F0}%";

    // X-axis caption: which loop steps the window currently shows, of the total —
    // labels the step axis and tracks the slider. "steps 1–N" when the whole loop
    // fits.
    public string StepRangeText
    {
        get
        {
            int n = HpLow.Count;
            if (n == 0) return "no loop steps recorded yet";
            int first = WindowOffset + 1;
            int last = Math.Min(n, WindowOffset + Math.Min(StepViewWindow, n));
            return first <= 1 && last >= n ? $"steps 1–{n}" : $"steps {first}–{last} of {n}";
        }
    }

    // The anchored step (1-based) for the in-graph readout + the scrub cursor.
    public string CenterStepText => HpLow.Count > 0 ? $"step {CursorIndex + 1}" : string.Empty;

    // Intermediate y-axis tick labels between the adaptive floor (AxisMin) and 100%,
    // at quarter, half, and three-quarter of the range, so the graph reads at a
    // glance rather than only at its two ends.
    public string AxisQ1Text  => $"{AxisMin + 0.25 * (100 - AxisMin):F0}%";
    public string AxisMidText => $"{AxisMin + 0.50 * (100 - AxisMin):F0}%";
    public string AxisQ3Text  => $"{AxisMin + 0.75 * (100 - AxisMin):F0}%";

    // Current headline rate, printed in each graph header so the number is
    // legible without eyeballing the curve — it equals the curve's right-most
    // point by construction (both are total ÷ time online).
    public string KillsRateText => $"{Activity.KillsPerHour:F1}/hr";
    public string ExpRateText   => $"{RateLabel(Activity.ExperiencePerHour, compact: true)}/hr";

    // ----- Time to level -----------------------------------------------
    // Countdown to the next level the running exp hasn't reached yet, at the
    // session exp/hour rate — honouring MajorMUD level banking, where exp keeps
    // accruing past the trained level. "N level(s) gained" is the count of
    // banked-but-untrained levels; the ETA then targets the first level above
    // that ceiling. So at trained level 5 with enough banked to train to 6 this
    // reads "1 level gained · HH:MM:SS until level 7". Rate comes from the same
    // whole-session average ExpRateText prints, so the two stay consistent.
    public string TimeToLevelText
    {
        get
        {
            if (_stats.Level <= 0) return "level unknown — type stat";

            // The shared TNL clock (AppServices.SelfTimeToLevel) — the status bar and the
            // Party window's self row read the same countdown, so all three agree.
            (TimeToLevelEstimator.Result r, TimeSpan? remaining) = _tnl;
            if (r.TargetLevel <= 0) return "exp chart unavailable — import game data";

            string bankedPart = $"{r.BankableLevels} level{(r.BankableLevels == 1 ? "" : "s")} gained";
            string etaPart = remaining is not { } eta
                ? "rate unknown"
                : eta <= TimeSpan.Zero ? "ready to level"
                    : $"{Fmt(eta)} until level {r.TargetLevel}";

            return $"{bankedPart} · {etaPart}";
        }
    }

    // Exp still to earn for the level the countdown targets, with that level —
    // MegaMUD's "Exp. needed". Banked-aware like the countdown, so it's the first
    // level the running exp hasn't reached, not merely the next one to train.
    public string ExpNeededText => _stats.Level > 0 && _tnl.Estimate.TargetLevel > 0
        ? $"{_tnl.Estimate.ExpNeeded:N0} (L{_tnl.Estimate.TargetLevel})"
        : "—";

    // The same countdown as TimeToLevelText, on its own row.
    public string WillLevelInText
    {
        get
        {
            if (_stats.Level <= 0 || _tnl.Estimate.TargetLevel <= 0) return "—";
            return _tnl.Remaining switch
            {
                null => "rate unknown",
                { } eta when eta <= TimeSpan.Zero => "ready to level",
                { } eta => Fmt(eta),
            };
        }
    }

    private static double Peak(IReadOnlyList<double> s)  => s.Count > 0 ? s.Max() : 0;
    private static double Floor(IReadOnlyList<double> s) => s.Count > 0 ? s.Min() : 0;

    private static string RateLabel(double v, bool compact)
    {
        if (v <= 0) return "0";
        return compact ? RateText.Compact(v) : v.ToString("F0");
    }

    // ----- Commands ----------------------------------------------------

    // Top-bar "Reset session" — wipes every session tracker's counters and
    // restarts their clocks. The transaction ledger is deliberately left intact:
    // it is user-owned and cleared only from its own window's Clear button. The
    // resets raise Changed, which refreshes the bound figures.
    [RelayCommand]
    private void Reset()
    {
        _combatTracker.Reset();
        _timeTracker.Reset();
        _activityTracker.Reset();
        _hpMaTracker.Reset();
        _lapBase = _runner.CompletedLaps;
    }

    // Per-section resets, one per collapsible. Each wipes only its own section's
    // figures so a user can re-anchor one dimension without losing the rest.

    // "Player Statistics" reset — the combat tally (hit / miss / crit / etc.).
    [RelayCommand]
    private void ResetPlayerStats() => _combatTracker.Reset();

    // "Time Analysis" reset — every line under that section: the time breakdown,
    // Sneak, Disarm Trap and Walk, and the loop laps. The per-hour rates sit under Session
    // Statistics and restart with its reset instead.
    [RelayCommand]
    private void ResetTimeAnalysis()
    {
        _timeTracker.Reset();
        _activityTracker.ResetMovement();
        _lapBase = _runner.CompletedLaps;
        OnPropertyChanged(nameof(LapCount));
        OnPropertyChanged(nameof(LastLapText));
        OnPropertyChanged(nameof(AverageLapText));
    }

    // "Session Statistics" reset — the running totals and their rates (kills,
    // experience, currency collected / stashed, and the two rate sparklines).
    [RelayCommand]
    private void ResetSessionStats() => _activityTracker.ResetTotals();

    // "Transaction history" button — opens the modeless ledger window (bank
    // deposits + stash-room hides recorded this session).
    [RelayCommand]
    private void OpenTransactionHistory() => _openTransactionHistory();

    // "Players Seen" button — opens the modeless per-character log of players
    // encountered in the world (Also-here matches + room walk-ins).
    [RelayCommand]
    private void OpenPlayersSeen() => _openPlayersSeen();

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

        HpMaHistoryStats hpMa = _hpMaTracker.Snapshot();
        HpLow = hpMa.HpLow;
        HpHigh = hpMa.HpHigh;
        HpAvg = hpMa.HpAvg;
        MaLow = hpMa.MaLow;
        MaHigh = hpMa.MaHigh;
        MaAvg = hpMa.MaAvg;
        HasManaHistory = hpMa.HasMana;
        LowestHpPercent = hpMa.LowestHpPercent;
        LowestMaPercent = hpMa.LowestMaPercent;
        // A shorter loop (or a reset) can pull the max in under the current focus;
        // keep the anchored step from stranding past the tail.
        if (FocusStep > StepViewMax) FocusStep = StepViewMax;

        // The countdown reads live PlayerStats + the wall clock, so it must
        // re-fire every tick even when the Activity snapshot compares equal.
        _tnl = _selfTimeToLevel();
        OnPropertyChanged(nameof(TimeToLevelText));
        OnPropertyChanged(nameof(ExpNeededText));
        OnPropertyChanged(nameof(WillLevelInText));

        // Lap readouts read the runner live (current lap ticks up each second);
        // re-fire them every tick, same as the countdown above.
        OnPropertyChanged(nameof(IsLoopRunning));
        OnPropertyChanged(nameof(LapLoopName));
        OnPropertyChanged(nameof(LapCount));
        OnPropertyChanged(nameof(LastLapText));
        OnPropertyChanged(nameof(AverageLapText));
        OnPropertyChanged(nameof(CurrentLapText));
        OnPropertyChanged(nameof(LapStartRoomText));
    }

    // ----- Loop laps ---------------------------------------------------
    // Read live off the LoopRunner each 1s tick (Refresh raises PropertyChanged for
    // all of these). A "lap" is one full completion of a running loop. While a loop
    // runs these reflect the current run; idle shows — (the runner clears its lap
    // history on stop). The runner already tracks durations, count, and the
    // circle-start room; the graph resolves that room's key to a display name.
    public bool IsLoopRunning => _runner.State != LoopState.Idle;
    public string LapLoopName => _runner.CurrentLoop?.Name ?? _runner.LastRunLoopName ?? "—";
    //
    // Time Analysis's Reset can't clear the runner's own count (the Roomba sweep
    // reads it), so it records where the count stood and these show only the laps
    // since. A new run restarts the runner's count below that mark, which drops it.
    public int LapCount
    {
        get
        {
            if (_runner.CompletedLaps < _lapBase) _lapBase = 0;
            return _runner.CompletedLaps - _lapBase;
        }
    }

    // The laps since the reset still in the runner's recent history, newest last.
    private IEnumerable<TimeSpan> RecentLaps
        => _runner.LapHistory.TakeLast(Math.Min(LapCount, _runner.LapHistory.Count));

    public string LastLapText => RecentLaps.Any() ? Fmt(RecentLaps.Last()) : "—";
    public string AverageLapText => RecentLaps.Any()
        ? Fmt(TimeSpan.FromTicks((long)RecentLaps.Average(t => t.Ticks)))
        : "—";
    public string CurrentLapText => IsLoopRunning ? Fmt(_runner.CurrentLapTime) : "—";
    public string LapStartRoomText => _runner.CircleStartRoom is { } k
        ? (_graph.GetRoom(k) is { } r ? $"{r.DisplayName} · {k}" : k.ToString())
        : "—";

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
        _hpMaTracker.Changed -= OnChanged;
    }
}
