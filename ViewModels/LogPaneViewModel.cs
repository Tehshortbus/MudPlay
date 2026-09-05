using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Services;

namespace MudPlay.ViewModels;

// Live view of LogService. Subscribes to LogService.EntryAdded on the
// producer's thread, marshals to the dispatcher, applies the current filter,
// and appends to the displayed list.
//
// Filter changes (severity checkboxes / search text) trigger a full rebuild
// from LogService.Snapshot. The ring's 2000-entry cap keeps this cheap; if
// profiling shows the rebuild churning, switch to an in-place predicate
// update.
public sealed partial class LogPaneViewModel : ObservableObject, IDisposable
{
    // Hard cap on Rows. Matches the underlying LogService ring capacity — the
    // displayed list can't usefully hold more than the snapshot can ever
    // serve. Without this cap, the OC grew unbounded as new entries flowed in
    // (the bind path didn't recycle rows on Rebuild), and toggling a filter
    // cleared a 30k-row list then re-added a few thousand which cascaded into
    // a UI lockup via the auto-scroll handler running per-Add.
    public const int MaxRows = LogService.DefaultCapacity;

    private readonly LogService _log;
    private readonly LogDiagnosticState? _diagnostics;
    private readonly Dictionary<LogSeverity, IBrush> _severityBrushes;
    private bool _suppressDiagnosticEcho;
    private bool _bulkUpdate;       // true during Rebuild — gates per-row UI side effects
    private bool _disposed;

    public ObservableCollection<LogPaneRowViewModel> Rows { get; } = new();

    // true while Rebuild is mid-execution. The LogPaneWindow's per-Add
    // ScrollIntoView handler uses this to skip the cascading scrolls during a
    // rebuild — one final scroll after the rebuild ends is enough.
    public bool IsBulkUpdating => _bulkUpdate;

    // Display-filter toggles for the always-on record levels — each defaults
    // to "show". Setting any of them re-runs the filter against the live
    // snapshot. Debug / Combat rows aren't display-filtered here; they follow
    // their generation toggles (DebugDiagnostics / CombatDiagnostics).
    [ObservableProperty] private bool _showInfo  = true;
    [ObservableProperty] private bool _showWarn  = true;
    [ObservableProperty] private bool _showError = true;

    [ObservableProperty] private string _searchText = string.Empty;

    // Generation toggle for the Debug channel. Mirrors
    // LogDiagnosticState.DebugDiagnostics — flipping it makes every
    // _log?.Debug(...) site across the engines start (or stop) emitting, AND
    // shows/hides the Debug rows already in the ring. Persisted per-character
    // via AppServices. Off by default — verbose tracing is a troubleshooting
    // affordance, not a per-session default.
    [ObservableProperty] private bool _debugDiagnostics;

    // Generation toggle for the Combat channel. Mirrors
    // LogDiagnosticState.CombatDiagnostics — flipping it gates the
    // combat-decision trace channel, and shows/hides the Combat rows already in
    // the ring. Persisted per-character via AppServices. Off by default.
    [ObservableProperty] private bool _combatDiagnostics;

    // Toggle for the on-disk diagnostic files. Mirrors
    // LogDiagnosticState.AutoCollectLogs — flipping it opens/closes the
    // program, memory, and combat-trace writers under Data/Logs. Unlike the two
    // above it does NOT touch displayed rows, so it drives no Rebuild.
    // Persisted per-character via AppServices. Off by default.
    [ObservableProperty] private bool _autoCollectLogs;

    // Toggle for the navigation hop-timing calibration trace. Mirrors
    // LogDiagnosticState.HopTiming — flipping it gates HopTimingCalibrator,
    // which emits one Info line per confirmed room hop. Like AutoCollectLogs it
    // doesn't touch displayed rows (the lines it emits show up through the
    // normal Info channel), so no Rebuild. Persisted per-character. Off by default.
    [ObservableProperty] private bool _hopTiming;

    // Toggle for unrecognized-message capture. Mirrors
    // LogDiagnosticState.CaptureUnrecognizedMessages — flipping it gates
    // Game.MessageCandidateWatcher. Like AutoCollectLogs/HopTiming it doesn't
    // touch displayed rows (the Warn rows it emits show up through the normal
    // channel), so no Rebuild. Persisted per-character. On by default.
    [ObservableProperty] private bool _captureUnrecognizedMessages;

    // Reveals the Death Recovery tab's "Simulate Death" test button. Mirrors
    // LogDiagnosticState.ShowSimulateDeath — session-only (off every launch) so a
    // normal user never sees the button; a tester flips it on here. Doesn't touch
    // displayed rows, so no Rebuild.
    [ObservableProperty] private bool _showSimulateDeath;

    // Reveals the Chest Offload window's "Simulate Chest" test button. Mirrors
    // LogDiagnosticState.ShowSimulateChest — session-only (off every launch), same
    // contract as ShowSimulateDeath. Doesn't touch displayed rows, so no Rebuild.
    [ObservableProperty] private bool _showSimulateChest;

    // Reveals the Unrecognized Lines tab's "Simulate entry" test button. Mirrors
    // LogDiagnosticState.ShowSimulateUnrecognized — session-only, same contract.
    [ObservableProperty] private bool _showSimulateUnrecognized;

    // When true, every appended row scrolls the list to the bottom. The XAML
    // hooks the actual scroll-into-view call; this flag gates it.
    [ObservableProperty] private bool _autoScroll = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private int _matchCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private int _logTotalCount;

    // "{matched:N0} / {total:N0} entries". Both counters are pre-cached
    // observable ints — reading StatusText must NOT call LogService.Snapshot,
    // which allocates a fresh 2000-entry array. Update LogTotalCount
    // alongside any path that mutates the live ring count.
    public string StatusText => $"{MatchCount:N0} / {LogTotalCount:N0} entries";

    public LogPaneViewModel(LogService log, Application app)
        : this(log, app, diagnostics: null) { }

    // Overload that binds the pane to a session-shared LogDiagnosticState so
    // the CombatDiagnostics toggle is the live umbrella for combat-related
    // verbose tracing (consumed by Game.Combat.RoundDamageTracker). Without
    // the binding, the pane is purely a viewer and the toggle is local to
    // this window — fine for tests, but the running app should always pass
    // AppServices.LogDiagnostics.
    public LogPaneViewModel(LogService log, Application app, LogDiagnosticState? diagnostics)
    {
        _log = log;
        _diagnostics = diagnostics;
        _severityBrushes = BuildSeverityBrushMap(app);

        if (_diagnostics is not null)
        {
            _suppressDiagnosticEcho = true;
            _debugDiagnostics  = _diagnostics.DebugDiagnostics;
            _combatDiagnostics = _diagnostics.CombatDiagnostics;
            _autoCollectLogs   = _diagnostics.AutoCollectLogs;
            _hopTiming         = _diagnostics.HopTiming;
            _captureUnrecognizedMessages = _diagnostics.CaptureUnrecognizedMessages;
            _showSimulateDeath = _diagnostics.ShowSimulateDeath;
            _showSimulateChest = _diagnostics.ShowSimulateChest;
            _showSimulateUnrecognized = _diagnostics.ShowSimulateUnrecognized;
            _suppressDiagnosticEcho = false;
            _diagnostics.Changed += OnDiagnosticsChanged;
        }

        Rebuild();
        _log.EntryAdded += OnEntryAdded;
    }

    private void OnDiagnosticsChanged()
    {
        // Another window (or a profile load) changed a toggle — mirror both
        // here without echoing back into _diagnostics (avoid a feedback loop).
        Dispatcher.UIThread.Post(() =>
        {
            if (_diagnostics is null) return;
            if (DebugDiagnostics != _diagnostics.DebugDiagnostics)
            {
                _suppressDiagnosticEcho = true;
                DebugDiagnostics = _diagnostics.DebugDiagnostics;
                _suppressDiagnosticEcho = false;
            }
            if (CombatDiagnostics != _diagnostics.CombatDiagnostics)
            {
                _suppressDiagnosticEcho = true;
                CombatDiagnostics = _diagnostics.CombatDiagnostics;
                _suppressDiagnosticEcho = false;
            }
            if (AutoCollectLogs != _diagnostics.AutoCollectLogs)
            {
                _suppressDiagnosticEcho = true;
                AutoCollectLogs = _diagnostics.AutoCollectLogs;
                _suppressDiagnosticEcho = false;
            }
            if (HopTiming != _diagnostics.HopTiming)
            {
                _suppressDiagnosticEcho = true;
                HopTiming = _diagnostics.HopTiming;
                _suppressDiagnosticEcho = false;
            }
            if (CaptureUnrecognizedMessages != _diagnostics.CaptureUnrecognizedMessages)
            {
                _suppressDiagnosticEcho = true;
                CaptureUnrecognizedMessages = _diagnostics.CaptureUnrecognizedMessages;
                _suppressDiagnosticEcho = false;
            }
            if (ShowSimulateDeath != _diagnostics.ShowSimulateDeath)
            {
                _suppressDiagnosticEcho = true;
                ShowSimulateDeath = _diagnostics.ShowSimulateDeath;
                _suppressDiagnosticEcho = false;
            }
            if (ShowSimulateChest != _diagnostics.ShowSimulateChest)
            {
                _suppressDiagnosticEcho = true;
                ShowSimulateChest = _diagnostics.ShowSimulateChest;
                _suppressDiagnosticEcho = false;
            }
            if (ShowSimulateUnrecognized != _diagnostics.ShowSimulateUnrecognized)
            {
                _suppressDiagnosticEcho = true;
                ShowSimulateUnrecognized = _diagnostics.ShowSimulateUnrecognized;
                _suppressDiagnosticEcho = false;
            }
        });
    }

    partial void OnDebugDiagnosticsChanged(bool value)
    {
        // Two effects: show/hide the Debug rows AND push the generation flag
        // so every _log?.Debug(...) site across the engines starts/stops.
        Rebuild();
        if (_suppressDiagnosticEcho) return;
        if (_diagnostics is null) return;
        _diagnostics.DebugDiagnostics = value;
    }

    partial void OnCombatDiagnosticsChanged(bool value)
    {
        // Two effects: show/hide the Combat rows AND push the generation flag
        // so the combat-decision channel + RoundDamageTracker's per-round
        // trace file react.
        Rebuild();
        if (_suppressDiagnosticEcho) return;
        if (_diagnostics is null) return;
        _diagnostics.CombatDiagnostics = value;
    }

    partial void OnAutoCollectLogsChanged(bool value)
    {
        // Only gates the on-disk writers — no displayed rows change — so no Rebuild.
        if (_suppressDiagnosticEcho) return;
        if (_diagnostics is null) return;
        _diagnostics.AutoCollectLogs = value;
    }

    partial void OnHopTimingChanged(bool value)
    {
        // Only gates the calibrator's Info emission — no displayed rows change — so no Rebuild.
        if (_suppressDiagnosticEcho) return;
        if (_diagnostics is null) return;
        _diagnostics.HopTiming = value;
    }

    partial void OnCaptureUnrecognizedMessagesChanged(bool value)
    {
        // Only gates the watcher's capture — no displayed rows change — so no Rebuild.
        if (_suppressDiagnosticEcho) return;
        if (_diagnostics is null) return;
        _diagnostics.CaptureUnrecognizedMessages = value;
    }

    partial void OnShowSimulateDeathChanged(bool value)
    {
        // Only gates the Death tab's test button visibility — no displayed rows change.
        if (_suppressDiagnosticEcho) return;
        if (_diagnostics is null) return;
        _diagnostics.ShowSimulateDeath = value;
    }

    partial void OnShowSimulateChestChanged(bool value)
    {
        // Only gates the Chest Offload window's test button visibility — no displayed rows change.
        if (_suppressDiagnosticEcho) return;
        if (_diagnostics is null) return;
        _diagnostics.ShowSimulateChest = value;
    }

    partial void OnShowSimulateUnrecognizedChanged(bool value)
    {
        // Only gates the Unrecognized Lines tab's test button visibility — no displayed rows change.
        if (_suppressDiagnosticEcho) return;
        if (_diagnostics is null) return;
        _diagnostics.ShowSimulateUnrecognized = value;
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

    // Append a row to Rows, evicting the oldest when the list would exceed
    // MaxRows. Stops the OC from growing past the ring's capacity and avoids
    // the multi-minute session leak where Rows held 100k+ entries while the
    // underlying ring only retained the last 2k.
    private void AppendCapped(LogPaneRowViewModel row)
    {
        while (Rows.Count >= MaxRows) Rows.RemoveAt(0);
        Rows.Add(row);
    }

    partial void OnShowInfoChanged(bool value)     => Rebuild();
    partial void OnShowWarnChanged(bool value)     => Rebuild();
    partial void OnShowErrorChanged(bool value)    => Rebuild();
    partial void OnSearchTextChanged(string value) => Rebuild();

    // Recompute Rows from the live log snapshot.
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

    // Fires once at the end of every Rebuild so the host window can do a
    // single ScrollIntoView on the newest row instead of the per-Add cascade.
    public event Action? BulkUpdateCompleted;

    private bool Passes(LogEntry entry)
    {
        if (!SeverityAllowed(entry.Severity)) return false;
        if (string.IsNullOrEmpty(SearchText)) return true;

        return entry.Message.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || entry.Source.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    private bool SeverityAllowed(LogSeverity s) => s switch
    {
        LogSeverity.Info   => ShowInfo,
        LogSeverity.Warn   => ShowWarn,
        LogSeverity.Error  => ShowError,
        // Debug / Combat rows follow their generation toggle: shown while the
        // channel is on, hidden once it's off (older rows linger in the ring).
        LogSeverity.Debug  => DebugDiagnostics,
        LogSeverity.Combat => CombatDiagnostics,
        _                  => true,
    };

    private LogPaneRowViewModel MakeRow(LogEntry e) => new(e, SeverityBrush);

    private IBrush SeverityBrush(LogSeverity s)
        => _severityBrushes.TryGetValue(s, out IBrush? brush) ? brush : Brushes.Gray;

    // Erase the displayed rows AND the underlying LogService ring.
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
            [LogSeverity.Debug]  = Lookup("SeverityDebugBrush"),
            [LogSeverity.Info]   = Lookup("SeverityInfoBrush"),
            [LogSeverity.Warn]   = Lookup("SeverityWarnBrush"),
            [LogSeverity.Error]  = Lookup("SeverityErrorBrush"),
            [LogSeverity.Combat] = Lookup("SeverityCombatBrush"),
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
