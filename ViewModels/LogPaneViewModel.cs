using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Services;

namespace FujinTerm.ViewModels;

/// <summary>
/// Live view of <see cref="LogService"/>. Subscribes to
/// <see cref="LogService.EntryAdded"/> on the producer's thread, marshals
/// to the dispatcher, applies the current filter, and appends to the
/// displayed list.
/// </summary>
/// <remarks>
/// Filter changes (severity checkboxes / search text) trigger a full
/// rebuild from <see cref="LogService.Snapshot"/>. The ring's 2000-entry
/// cap keeps this cheap; if profiling shows the rebuild churning, switch
/// to an in-place predicate update.
/// </remarks>
public sealed partial class LogPaneViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// Hard cap on <see cref="Rows"/>. Matches the underlying
    /// <see cref="LogService"/> ring capacity — the displayed list can't
    /// usefully hold more than the snapshot can ever serve. Without this
    /// cap, the OC grew unbounded as new entries flowed in (the bind
    /// path didn't recycle rows on Rebuild), and toggling a filter
    /// cleared a 30k-row list then re-added a few thousand which
    /// cascaded into a UI lockup via the auto-scroll handler running
    /// per-Add.
    /// </summary>
    public const int MaxRows = LogService.DefaultCapacity;

    private readonly LogService _log;
    private readonly LogDiagnosticState? _diagnostics;
    private readonly Dictionary<LogSeverity, IBrush> _severityBrushes;
    private bool _suppressDiagnosticEcho;
    private bool _bulkUpdate;       // true during Rebuild — gates per-row UI side effects
    private bool _disposed;

    public ObservableCollection<LogPaneRowViewModel> Rows { get; } = new();

    /// <summary>
    /// <c>true</c> while <see cref="Rebuild"/> is mid-execution. The
    /// LogPaneWindow's per-Add ScrollIntoView handler uses this to skip
    /// the cascading scrolls during a rebuild — one final scroll after
    /// the rebuild ends is enough.
    /// </summary>
    public bool IsBulkUpdating => _bulkUpdate;

    // Severity filter toggles — each defaults to "show". Setting any of them
    // re-runs the filter against the live snapshot.
    [ObservableProperty] private bool _showDebug   = true;
    [ObservableProperty] private bool _showInfo    = true;
    [ObservableProperty] private bool _showWarn    = true;
    [ObservableProperty] private bool _showError   = true;
    [ObservableProperty] private bool _showGameMsg = true;
    [ObservableProperty] private bool _showCmd     = true;

    [ObservableProperty] private string _searchText = string.Empty;

    /// <summary>
    /// Unified combat toggle (replaces the previous separate
    /// "Combat preset" filter and "Combat diagnostics" umbrella
    /// checkboxes — both expressed the same intent at different
    /// layers). When on:
    /// <list type="bullet">
    /// <item>Filter the displayed rows to the Phase 9 combat-engine
    /// source categories (<see cref="CombatSources"/>).</item>
    /// <item>Push to <see cref="LogDiagnosticState.CombatDiagnostics"/>
    /// so <see cref="Game.Combat.RoundDamageTracker"/> writes the
    /// per-round trace file (and future verbose Debug emitters
    /// switch on).</item>
    /// </list>
    /// Off by default — verbose tracing is a transient debugging
    /// affordance, not a per-session default.
    /// </summary>
    [ObservableProperty] private bool _combatFilter;

    /// <summary>
    /// Source-name set <see cref="CombatFilter"/> filters to. Sourced
    /// from <c>docs/10-phase-9-automation-engines.md</c> § Cross-cut 3
    /// — the categories Phase 9 engines emit under. Match is
    /// ordinal case-insensitive (the producer's tag wins; the filter
    /// doesn't care which case the engine chose).
    /// </summary>
    public static IReadOnlySet<string> CombatSources { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Combat", "CombatGate", "RoomClassifier",
            "Health", "HealthGate",
            "Casting", "CastSend",
            "Round", "Gate",
        };

    /// <summary>
    /// When true, every appended row scrolls the list to the bottom. The
    /// XAML hooks the actual scroll-into-view call; this flag gates it.
    /// </summary>
    [ObservableProperty] private bool _autoScroll = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private int _matchCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private int _logTotalCount;

    /// <summary>
    /// <c>"{matched:N0} / {total:N0} entries"</c>. Both counters are
    /// pre-cached observable ints — reading <see cref="StatusText"/>
    /// must NOT call <see cref="LogService.Snapshot"/>, which allocates
    /// a fresh 2000-entry array. Update <see cref="LogTotalCount"/>
    /// alongside any path that mutates the live ring count.
    /// </summary>
    public string StatusText => $"{MatchCount:N0} / {LogTotalCount:N0} entries";

    public LogPaneViewModel(LogService log, Application app)
        : this(log, app, diagnostics: null) { }

    /// <summary>
    /// Overload that binds the pane to a session-shared
    /// <see cref="LogDiagnosticState"/> so the
    /// <see cref="CombatDiagnostics"/> toggle is the live umbrella for
    /// combat-related verbose tracing (consumed by
    /// <see cref="Game.Combat.RoundDamageTracker"/>). Without the
    /// binding, the pane is purely a viewer and the toggle is local
    /// to this window — fine for tests, but the running app should
    /// always pass <see cref="AppServices.LogDiagnostics"/>.
    /// </summary>
    public LogPaneViewModel(LogService log, Application app, LogDiagnosticState? diagnostics)
    {
        _log = log;
        _diagnostics = diagnostics;
        _severityBrushes = BuildSeverityBrushMap(app);

        if (_diagnostics is not null)
        {
            _suppressDiagnosticEcho = true;
            _combatFilter = _diagnostics.CombatDiagnostics;
            _suppressDiagnosticEcho = false;
            _diagnostics.Changed += OnDiagnosticsChanged;
        }

        Rebuild();
        _log.EntryAdded += OnEntryAdded;
    }

    private void OnDiagnosticsChanged()
    {
        // Another window flipped the umbrella — mirror it here without
        // echoing back into _diagnostics (avoid a feedback loop).
        Dispatcher.UIThread.Post(() =>
        {
            if (_diagnostics is null) return;
            if (CombatFilter == _diagnostics.CombatDiagnostics) return;
            _suppressDiagnosticEcho = true;
            CombatFilter = _diagnostics.CombatDiagnostics;
            _suppressDiagnosticEcho = false;
        });
    }

    partial void OnCombatFilterChanged(bool value)
    {
        // Two effects: refilter the displayed rows AND push the
        // umbrella flag so RoundDamageTracker + future verbose
        // emitters react.
        Rebuild();
        if (_suppressDiagnosticEcho) return;
        if (_diagnostics is null) return;
        _diagnostics.CombatDiagnostics = value;
    }

    private void OnEntryAdded(LogEntry entry)
    {
        // Producer thread; marshal to UI.
        Dispatcher.UIThread.Post(() =>
        {
            // Single bounded source of truth for "how many entries are
            // in the ring" — read once per add, not per StatusText get.
            int total = _log.Count;
            if (LogTotalCount != total) LogTotalCount = total;

            if (!Passes(entry)) return;
            AppendCapped(MakeRow(entry));
            MatchCount = Rows.Count;
        });
    }

    /// <summary>
    /// Append a row to <see cref="Rows"/>, evicting the oldest when
    /// the list would exceed <see cref="MaxRows"/>. Stops the OC from
    /// growing past the ring's capacity and avoids the multi-minute
    /// session leak where Rows held 100k+ entries while the underlying
    /// ring only retained the last 2k.
    /// </summary>
    private void AppendCapped(LogPaneRowViewModel row)
    {
        while (Rows.Count >= MaxRows) Rows.RemoveAt(0);
        Rows.Add(row);
    }

    partial void OnShowDebugChanged(bool value)   => Rebuild();
    partial void OnShowInfoChanged(bool value)    => Rebuild();
    partial void OnShowWarnChanged(bool value)    => Rebuild();
    partial void OnShowErrorChanged(bool value)   => Rebuild();
    partial void OnShowGameMsgChanged(bool value) => Rebuild();
    partial void OnShowCmdChanged(bool value)     => Rebuild();
    partial void OnSearchTextChanged(string value) => Rebuild();

    /// <summary>Recompute <see cref="Rows"/> from the live log snapshot.</summary>
    private void Rebuild()
    {
        // _bulkUpdate gates the per-row auto-scroll in the LogPaneWindow
        // code-behind. Without this gate, toggling a filter cleared a
        // long Rows list then re-added several thousand rows, each
        // forcing ScrollIntoView and a layout pass — that's the lockup
        // the user hit. After the rebuild a single ScrollIntoView on
        // the newest row replaces the cascade.
        _bulkUpdate = true;
        try
        {
            Rows.Clear();
            int added = 0;
            foreach (LogEntry entry in _log.Snapshot())
            {
                if (!Passes(entry)) continue;
                if (added >= MaxRows) break;  // safety — Snapshot should already be capped
                Rows.Add(MakeRow(entry));
                added++;
            }
            MatchCount = Rows.Count;
            LogTotalCount = _log.Count;
        }
        finally
        {
            _bulkUpdate = false;
        }
        BulkUpdateCompleted?.Invoke();
    }

    /// <summary>
    /// Fires once at the end of every <see cref="Rebuild"/> so the host
    /// window can do a single ScrollIntoView on the newest row instead
    /// of the per-Add cascade.
    /// </summary>
    public event Action? BulkUpdateCompleted;

    private bool Passes(LogEntry entry)
    {
        if (!SeverityAllowed(entry.Severity)) return false;
        if (CombatFilter && !CombatSources.Contains(entry.Source)) return false;
        if (string.IsNullOrEmpty(SearchText)) return true;

        return entry.Message.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || entry.Source.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    private bool SeverityAllowed(LogSeverity s) => s switch
    {
        LogSeverity.Debug   => ShowDebug,
        LogSeverity.Info    => ShowInfo,
        LogSeverity.Warn    => ShowWarn,
        LogSeverity.Error   => ShowError,
        LogSeverity.GameMsg => ShowGameMsg,
        LogSeverity.Cmd     => ShowCmd,
        _                   => true,
    };

    private LogPaneRowViewModel MakeRow(LogEntry e) => new(e, SeverityBrush);

    private IBrush SeverityBrush(LogSeverity s)
        => _severityBrushes.TryGetValue(s, out IBrush? brush) ? brush : Brushes.Gray;

    /// <summary>Erase the displayed rows AND the underlying LogService ring.</summary>
    [RelayCommand]
    private void Clear()
    {
        _log.Clear();
        Rows.Clear();
        MatchCount = 0;
        LogTotalCount = 0;
    }

    private static Dictionary<LogSeverity, IBrush> BuildSeverityBrushMap(Application app)
    {
        IBrush Lookup(string key)
            => app.TryGetResource(key, null, out object? value) && value is IBrush brush
                ? brush
                : Brushes.Gray;

        return new()
        {
            [LogSeverity.Debug]   = Lookup("SeverityDebugBrush"),
            [LogSeverity.Info]    = Lookup("SeverityInfoBrush"),
            [LogSeverity.Warn]    = Lookup("SeverityWarnBrush"),
            [LogSeverity.Error]   = Lookup("SeverityErrorBrush"),
            [LogSeverity.GameMsg] = Lookup("SeverityGameMsgBrush"),
            [LogSeverity.Cmd]     = Lookup("SeverityCmdBrush"),
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _log.EntryAdded -= OnEntryAdded;
        if (_diagnostics is not null) _diagnostics.Changed -= OnDiagnosticsChanged;
    }
}
