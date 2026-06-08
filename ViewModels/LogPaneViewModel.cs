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
    private readonly LogService _log;
    private readonly LogDiagnosticState? _diagnostics;
    private readonly Dictionary<LogSeverity, IBrush> _severityBrushes;
    private bool _suppressDiagnosticEcho;
    private bool _disposed;

    public ObservableCollection<LogPaneRowViewModel> Rows { get; } = new();

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
    /// One-click "Combat preset" — when on, filters the displayed rows to the
    /// Phase 9 combat-engine source categories
    /// (<see cref="CombatPresetSources"/>). When off, every source passes.
    /// Defaults off so the normal multi-subsystem view is unchanged.
    /// </summary>
    [ObservableProperty] private bool _combatPreset;

    /// <summary>
    /// Source-name set that <see cref="CombatPreset"/> filters to. Sourced
    /// from <c>docs/10-phase-9-automation-engines.md</c> § Cross-cut 3
    /// — the categories Phase 9 engines emit under. Match is
    /// ordinal case-insensitive (the producer's tag wins; the preset
    /// doesn't care which case the engine chose).
    /// </summary>
    public static IReadOnlySet<string> CombatPresetSources { get; } =
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

    /// <summary>
    /// Mirror of <see cref="LogDiagnosticState.CombatDiagnostics"/> —
    /// session-only umbrella that gates the per-round combat trace
    /// file + (future) verbose Debug emission from the combat-engine
    /// categories. Lives on the Log pane menu rather than per-character
    /// settings because verbose tracing is a transient debugging
    /// affordance, not a per-character preference. Off by default.
    /// </summary>
    [ObservableProperty] private bool _combatDiagnostics;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private int _matchCount;

    public string StatusText => $"{MatchCount:N0} / {_log.Snapshot().Length:N0} entries";

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
            _combatDiagnostics = _diagnostics.CombatDiagnostics;
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
            if (CombatDiagnostics == _diagnostics.CombatDiagnostics) return;
            _suppressDiagnosticEcho = true;
            CombatDiagnostics = _diagnostics.CombatDiagnostics;
            _suppressDiagnosticEcho = false;
        });
    }

    partial void OnCombatDiagnosticsChanged(bool value)
    {
        if (_suppressDiagnosticEcho) return;
        if (_diagnostics is null) return;
        _diagnostics.CombatDiagnostics = value;
    }

    private void OnEntryAdded(LogEntry entry)
    {
        // Producer thread; marshal to UI.
        Dispatcher.UIThread.Post(() =>
        {
            if (!Passes(entry)) return;
            Rows.Add(MakeRow(entry));
            MatchCount = Rows.Count;
            OnPropertyChanged(nameof(StatusText));
        });
    }

    partial void OnShowDebugChanged(bool value)   => Rebuild();
    partial void OnShowInfoChanged(bool value)    => Rebuild();
    partial void OnShowWarnChanged(bool value)    => Rebuild();
    partial void OnShowErrorChanged(bool value)   => Rebuild();
    partial void OnShowGameMsgChanged(bool value) => Rebuild();
    partial void OnShowCmdChanged(bool value)     => Rebuild();
    partial void OnSearchTextChanged(string value) => Rebuild();
    partial void OnCombatPresetChanged(bool value) => Rebuild();

    /// <summary>Recompute <see cref="Rows"/> from the live log snapshot.</summary>
    private void Rebuild()
    {
        Rows.Clear();
        foreach (LogEntry entry in _log.Snapshot())
        {
            if (Passes(entry)) Rows.Add(MakeRow(entry));
        }
        MatchCount = Rows.Count;
        OnPropertyChanged(nameof(StatusText));
    }

    private bool Passes(LogEntry entry)
    {
        if (!SeverityAllowed(entry.Severity)) return false;
        if (CombatPreset && !CombatPresetSources.Contains(entry.Source)) return false;
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
        OnPropertyChanged(nameof(StatusText));
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
