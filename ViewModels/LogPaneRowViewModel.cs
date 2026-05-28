using Avalonia.Media;
using FujinTerm.Services;

namespace FujinTerm.ViewModels;

/// <summary>
/// One row in the <see cref="LogPaneViewModel"/>'s displayed list. Wraps a
/// <see cref="LogEntry"/> with the formatted display strings the XAML binds
/// to, plus the severity-coloured badge brush.
/// </summary>
/// <remarks>
/// Why a class wrapping the struct rather than binding the struct directly:
/// Avalonia's compiled bindings prefer reference types for list-item view
/// models, and the small UI-only formatting (HH:mm:ss timestamp, severity
/// abbreviation, badge brush lookup) is cleaner here than in XAML
/// converters.
/// </remarks>
public sealed class LogPaneRowViewModel
{
    public LogEntry Entry { get; }

    public string TimestampText { get; }
    public string SeverityText { get; }
    public IBrush SeverityBrush { get; }
    public string Source => Entry.Source;
    public string Message => Entry.Message;

    public LogPaneRowViewModel(LogEntry entry, Func<LogSeverity, IBrush> brushLookup)
    {
        Entry = entry;
        TimestampText = entry.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");
        SeverityText = SeverityAbbrev(entry.Severity);
        SeverityBrush = brushLookup(entry.Severity);
    }

    private static string SeverityAbbrev(LogSeverity s) => s switch
    {
        LogSeverity.Debug   => "DBG",
        LogSeverity.Info    => "INF",
        LogSeverity.Warn    => "WRN",
        LogSeverity.Error   => "ERR",
        LogSeverity.GameMsg => "GAM",
        LogSeverity.Cmd     => "CMD",
        _                   => "?",
    };
}
