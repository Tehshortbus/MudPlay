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
    private readonly Dictionary<LogSeverity, IBrush> _severityBrushes;
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
    /// When true, every appended row scrolls the list to the bottom. The
    /// XAML hooks the actual scroll-into-view call; this flag gates it.
    /// </summary>
    [ObservableProperty] private bool _autoScroll = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private int _matchCount;

    public string StatusText => $"{MatchCount:N0} / {_log.Snapshot().Length:N0} entries";

    public LogPaneViewModel(LogService log, Application app)
    {
        _log = log;
        _severityBrushes = BuildSeverityBrushMap(app);

        Rebuild();
        _log.EntryAdded += OnEntryAdded;
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
    }
}
