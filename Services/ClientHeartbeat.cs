namespace MudPlay.Services;

// The two things the program log says about the client itself, so the log a hang
// or a forced kill leaves behind can be read without the client: a block at
// startup (what is running, on what), and a status line every few minutes (how
// long it has been up, whether it is in the game, what is in control and what
// holds it). Both at Info, with no setting: they cost a line.
public sealed class ClientHeartbeat
{
    public const string LogSource = "Client";

    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(3);

    private readonly LogService _log;
    private readonly Func<string> _status;
    private DateTimeOffset _next;

    // status: the changing half of the line (connection, engine in control,
    // gates), read when a line is due.
    public ClientHeartbeat(LogService log, Func<string> status)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _status = status ?? throw new ArgumentNullException(nameof(status));
        _next = DateTimeOffset.Now + Interval;
    }

    // What the client is and runs on. The same lines open each last-minutes file.
    public static IReadOnlyList<string> StartupLines(string? activeSet, string? lastProfile) =>
    [
        .. ClientLines(),
        $"data folder {BugReportContext.HomeRelative(AppPaths.DataRoot)}; game data {activeSet ?? "(none yet)"}; "
            + $"last profile {lastProfile ?? "(none)"}",
    ];

    // The build, the system and the process: the half of the startup block that
    // every log file's header repeats (LogFileHeader).
    public static IReadOnlyList<string> ClientLines() =>
    [
        $"MudPlay {ClientEnvironment.Build}",
        $"{ClientEnvironment.Os} ({ClientEnvironment.Architecture}); {ClientEnvironment.Desktop}; drawn with {ClientEnvironment.Drawing}; {ClientEnvironment.Runtime}",
        $"process {ClientEnvironment.ProcessId} started {ClientEnvironment.ProcessStart:yyyy-MM-dd HH:mm:ss zzz}",
    ];

    // The changing half of a status line, read off the running client.
    public static string Describe(AppServices svc)
    {
        AppServices.ConnectionSnapshot link = svc.Connection;
        IReadOnlyCollection<string> gates = svc.MovementCoordinator.AssertedGates;
        return (link.Connected ? "connected" : link.Connecting ? "connecting" : "not connected")
            + (svc.InGameCapture.InGame ? ", in the game" : svc.InGameCapture.AtBoardMenu ? ", at the board's menu" : ", not in the game")
            + $"; master switch {(svc.AutoModeController.KillSwitchEngaged ? "OFF" : "on")}"
            + $"; movement {svc.MovementControl.State} (walker {svc.Walker.State}, loop {svc.LoopRunner.State}, auto-lair {svc.AutoLair.Phase})"
            + $"; gates {(gates.Count > 0 ? string.Join(",", gates) : "none")}"
            + $"; send gate {(svc.EngineGate.IsLocked ? "held by " + string.Join(",", svc.EngineGate.Holds) : "open")}"
            + $"; windows {svc.Performance.OpenWindowsSummary}"
            + (svc.MemoryLog.LatestSummary is { } memory ? $"; memory log: {memory}" : string.Empty);
    }

    public void LogStartup(string? activeSet, string? lastProfile)
    {
        foreach (string line in StartupLines(activeSet, lastProfile)) _log.Info(LogSource, "startup: " + line);
    }

    // Called about once a second; logs when a line is due.
    public void Tick(DateTimeOffset now)
    {
        if (now < _next) return;
        _next = now + Interval;
        string status;
        // The status reads half the client's engines: one of them failing must
        // not take the timer that calls this down with it.
        try { status = _status(); }
        catch (Exception ex) { status = $"(status unreadable: {ex.GetType().Name}: {ex.Message})"; }
        _log.Info(LogSource, $"status: up {BugReportHoldsAndWaits.Age(ClientEnvironment.Uptime)}; {status}; "
            + $"{GC.GetTotalMemory(false) / (1024 * 1024)} MB managed, {Environment.WorkingSet / (1024 * 1024)} MB working set");
    }
}
