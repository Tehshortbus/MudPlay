
namespace MudPlay.Services;

// Writes the Session Statistics window's figures to their own file under Logs
// every few minutes, so a long unattended run leaves a record of how its rates
// and totals moved instead of only what the window shows at the end.
//
// Gated by LogDiagnosticState.LogSessionStatistics (the Program Log window's "Log
// session statistics" box, off by default). Blocks are written only while the
// character is in the game: a client parked at a login screen or the board's menu
// would otherwise repeat the same block all night. Leaving the game writes one
// last block, so the stretch since the previous one isn't lost.
//
// Lives on the UI thread, as the session trackers it reads do. A block is a few
// dozen short lines every few minutes, so the write stays on that thread.
public sealed class SessionStatsLog : IDisposable
{
    public const string LogSource = "SessionStatsLog";
    public const string Topic = "session-stats";

    private readonly LogDiagnosticState _diagnostics;
    private readonly Func<bool> _inGame;
    private readonly Func<DateTimeOffset, string, string> _snapshot;
    private readonly LogService? _log;
    private readonly Func<(Action<string> Write, Action Close, string Path)> _open;
    // Arms one tick after the interval; disposing what it returns cancels it.
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;
    private IDisposable? _tick;
    private TimeSpan _tickInterval;

    private (Action<string> Write, Action Close, string Path)? _file;
    private bool _wasOn;
    private bool _broken;
    private bool _disposed;

    // snapshot(at, why) is the block to write.
    public SessionStatsLog(LogDiagnosticState diagnostics, Func<bool> inGame,
        Func<DateTimeOffset, string, string> snapshot, LogService? log = null)
        : this(diagnostics, inGame, snapshot, log, schedule: null, open: null) { }

    // schedule stands in for the dispatcher's timer; open for the file.
    internal SessionStatsLog(LogDiagnosticState diagnostics, Func<bool> inGame,
        Func<DateTimeOffset, string, string> snapshot, LogService? log,
        Func<TimeSpan, Action, IDisposable>? schedule,
        Func<(Action<string> Write, Action Close, string Path)>? open)
    {
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _inGame = inGame ?? throw new ArgumentNullException(nameof(inGame));
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _log = log;
        _open = open ?? OpenFile;
        _schedule = schedule ?? DispatcherTicks.Once;
        _diagnostics.Changed += OnDiagnosticsChanged;
        OnDiagnosticsChanged();
    }

    // The file blocks are going to; null until the first one is written.
    public string? Path => _file?.Path;

    public bool IsOn => _diagnostics.LogSessionStatistics && !_broken;

    private TimeSpan Interval => TimeSpan.FromMinutes(_diagnostics.SessionStatisticsMinutes);

    // For the bug report: the file's name only, since its folder names the user.
    public string Summary =>
        _broken ? "on, but the file couldn't be written (see the program log)"
        : !_diagnostics.LogSessionStatistics ? "off"
        : $"on, every {_diagnostics.SessionStatisticsMinutes} min while in the game; file: "
            + (Path is { } path ? System.IO.Path.GetFileName(path) : "(not started yet)");

    private static (Action<string> Write, Action Close, string Path) OpenFile()
    {
        DebugLogWriter writer = new(Topic);
        return (writer.WriteLine, writer.Dispose, writer.Path);
    }

    private void OnDiagnosticsChanged()
    {
        if (_disposed) return;
        bool on = _diagnostics.LogSessionStatistics;
        // Only a changed interval touches a running timer: every toggle in the
        // window raises Changed, and re-arming it would start the count over.
        if (on && (_tick is null || _tickInterval != Interval)) StartTicking();
        else if (!on) StopTicking();
        if (on == _wasOn) return;
        _wasOn = on;
        if (on)
        {
            _broken = false;
            _log?.Info(LogSource,
                $"logging session statistics every {_diagnostics.SessionStatisticsMinutes} min while in the game.");
            // Switched on mid-session: a first block now, so the file has a starting point.
            if (_inGame()) Write("logging switched on");
        }
        else
        {
            if (_file is { } open) _log?.Info(LogSource, $"stopped; the file is {open.Path}");
            CloseFile();
        }
    }

    private void OnTimer()
    {
        if (_diagnostics.LogSessionStatistics && _inGame())
            Write($"every {_diagnostics.SessionStatisticsMinutes} min");
    }

    private void StartTicking()
    {
        _tick?.Dispose();
        _tickInterval = Interval;
        _tick = _schedule(_tickInterval, OnTick);
    }

    private void StopTicking()
    {
        _tick?.Dispose();
        _tick = null;
    }

    private void OnTick()
    {
        if (_disposed) return;
        _tick = _schedule(_tickInterval, OnTick);
        OnTimer();
    }

    // The character entered or left the game (InGameCapture).
    public void NoteInGameChanged(bool inGame)
    {
        if (_disposed || !_diagnostics.LogSessionStatistics) return;
        if (inGame)
        {
            // Count the interval from entering, not from whenever the timer last fired.
            if (_tick is not null) StartTicking();
            return;
        }
        Write("left the game");
    }

    private void Write(string why)
    {
        if (_broken || _disposed) return;
        try
        {
            if (_file is null)
            {
                _file = _open();
                _log?.Info(LogSource, $"writing to {_file.Value.Path}");
            }
            _file.Value.Write(_snapshot(DateTimeOffset.Now, why));
            _log?.Debug(LogSource, $"block written ({why}).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Disk full, folder gone, permissions. Stop for the session and say so
            // once: losing the record is acceptable, retrying every interval isn't.
            _broken = true;
            _log?.Warn(LogSource, $"couldn't write the session statistics log, stopping: {ex.Message}");
            CloseFile();
        }
    }

    private void CloseFile()
    {
        if (_file is not { } open) return;
        _file = null;
        try { open.Close(); }
        catch (IOException) { /* closing a file we are done with; nothing to recover */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopTicking();
        _diagnostics.Changed -= OnDiagnosticsChanged;
        CloseFile();
    }
}
