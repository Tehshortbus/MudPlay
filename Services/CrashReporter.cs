using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;

namespace FujinTerm.Services;

// Last-ditch crash capture. A fatal error during play (the combat-time crash
// that prompted this) otherwise vanishes without a trace: the process dies
// before anything reaches the program log, leaving the user no way to
// reconstruct what they were doing. Install() wires the CLR failure channels;
// on any of them we write a self-contained "Crash-<timestamp>.md" to the
// Desktop carrying the exception plus — when the UI is up — the same live-state
// snapshot a manual bug report captures, so a crash is always recoverable after
// the fact.
//
// Everything here is defensive to the extreme: a crash handler that throws
// takes the process down a second way and loses the report, so every step is
// wrapped and the file writer never propagates.
public static class CrashReporter
{
    private static int _installed;

    // Cap total reports so a cascading fault (e.g. an exception thrown from
    // inside the handler's own I/O, re-entering the same channel) can't carpet
    // the Desktop with files.
    private static int _written;
    private const int MaxReports = 5;

    private static Func<string?>? _stateProvider;

    // MainWindowViewModel registers a callback that renders the live client
    // state (scrollback, program log, player / party / engine state) as Markdown.
    // Optional: if it's unset or throws, the crash report still carries the
    // exception, just without the state dump.
    public static void RegisterStateProvider(Func<string?> provider) => _stateProvider = provider;

    // Wire the two out-of-band failure channels. UI-thread crashes escape the
    // Avalonia run loop instead and are caught by Guard.
    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) == 1) return;

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Report(e.ExceptionObject as Exception, "AppDomain.UnhandledException");

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            // A background DBus service-missing fault (Avalonia's clipboard /
            // freedesktop-portal integration on a desktop that doesn't run that
            // service) fire-and-forgets a Task that faults with
            // "ServiceUnknown: The name is not activatable" — it surfaces here as
            // unobserved. It's benign and out of our control, so mark it observed
            // and move on; dropping a "crash" report on the Desktop for it would
            // only alarm the user over a non-fatal background hiccup.
            if (!IsBenignBackgroundDBusFault(e.Exception))
                Report(e.Exception, "TaskScheduler.UnobservedTaskException");
            e.SetObserved();
        };
    }

    // Recognise the DBus service-missing fault that Avalonia raises in the
    // background on Linux desktops lacking the target service. Matched by type
    // name + message so we don't take a compile-time dependency on Tmds.DBus,
    // walking both the AggregateException fan-out and the InnerException chain.
    private static bool IsBenignBackgroundDBusFault(Exception? ex)
    {
        if (ex is null) return false;
        if (ex is AggregateException agg)
            return agg.Flatten().InnerExceptions.Any(IsBenignBackgroundDBusFault);
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if ((e.GetType().FullName ?? "").Contains("DBusException", StringComparison.Ordinal))
                return true;
            string msg = e.Message ?? "";
            if (msg.Contains("org.freedesktop.DBus.Error.ServiceUnknown", StringComparison.Ordinal)
             || msg.Contains("not activatable", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // Run the app under the crash net. An exception escaping the Avalonia run
    // loop (the common UI-thread crash path) is captured, then rethrown with its
    // original stack intact so the process still exits non-zero and nothing is
    // silently swallowed.
    public static void Guard(Action run)
    {
        ArgumentNullException.ThrowIfNull(run);
        try
        {
            run();
        }
        catch (Exception ex)
        {
            Report(ex, "UI run loop");
            ExceptionDispatchInfo.Capture(ex).Throw();
        }
    }

    public static void Report(Exception? ex, string source)
    {
        if (Interlocked.Increment(ref _written) > MaxReports) return;
        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            // Headless / service accounts can report no Desktop — fall back to
            // the app's working directory so the report still lands somewhere.
            if (string.IsNullOrEmpty(desktop)) desktop = AppContext.BaseDirectory;
            string path = Path.Combine(desktop, $"Crash-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.md");
            File.WriteAllText(path, Build(ex, source));
        }
        catch
        {
            // The crash handler must never throw. If we can't even write the
            // report there is nothing further we can safely do — the process is
            // already going down.
        }
    }

    internal static string Build(Exception? ex, string source)
    {
        StringBuilder sb = new(capacity: 16 * 1024);
        sb.Append("# FujinTerm crash report\n\n");
        sb.Append("_Captured ").Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz")).Append("_\n\n");

        sb.Append("## Fault\n\n");
        Kv(sb, "Version", Safe(() => AppInfo.Version));
        Kv(sb, "Source", source);
        Kv(sb, "OS", Safe(() => RuntimeInformation.OSDescription));
        Kv(sb, "Runtime", Safe(() => RuntimeInformation.FrameworkDescription));
        sb.Append('\n');

        sb.Append("## Exception\n\n");
        if (ex is null)
            sb.Append("_(no exception object — the CLR reported a fault with no detail)_\n\n");
        else
            AppendException(sb, ex);

        string? state = SafeState();
        if (!string.IsNullOrWhiteSpace(state))
            sb.Append(state);
        else
            sb.Append("## State\n\n_(client state unavailable — the crash happened before the UI was ready, or state capture itself failed)_\n");

        return sb.ToString();
    }

    // Walk the inner-exception chain so a wrapped fault (AggregateException, a
    // rethrow, etc.) shows every layer, not just the outermost message.
    private static void AppendException(StringBuilder sb, Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            sb.Append("**").Append(e.GetType().FullName).Append("**: ")
              .Append(e.Message).Append("\n\n```\n")
              .Append(e.StackTrace ?? "(no stack trace)").Append("\n```\n\n");
            if (e.InnerException is not null) sb.Append("Caused by:\n\n");
        }
    }

    private static string? SafeState()
    {
        try { return _stateProvider?.Invoke(); }
        catch { return null; }
    }

    private static string Safe(Func<string> read)
    {
        try { return read(); }
        catch { return "(unknown)"; }
    }

    private static void Kv(StringBuilder sb, string key, string value) =>
        sb.Append("- **").Append(key).Append("**: ").Append(value).Append('\n');
}
