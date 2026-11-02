using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace MudPlay.Services;

// What the running client is and what it runs on: the facts a reader of a bug
// report, or of the log a hang left behind, needs before anything else. Read once
// and cached; none of it changes while the process lives.
public static class ClientEnvironment
{
    // When this process started. A report whose client started before the build
    // that carries a fix is a report of the old code.
    public static DateTimeOffset ProcessStart { get; } = ReadProcessStart();

    public static TimeSpan Uptime => DateTimeOffset.Now - ProcessStart;

    public static int ProcessId { get; } = Environment.ProcessId;

#if DEBUG
    public const string BuildConfiguration = "Debug";
#else
    public const string BuildConfiguration = "Release";
#endif

    // The commit the build was made from, when the build recorded one (the part
    // of the informational version after '+'); null otherwise.
    public static string? Commit { get; } = ReadCommit();

    // When the client's own assembly was written: the nearest thing to a build
    // date that every build carries.
    public static DateTimeOffset? BuildTime { get; } = ReadBuildTime();

    public static string Os { get; } = Safe(() => RuntimeInformation.OSDescription);
    public static string Architecture { get; } = Safe(() => RuntimeInformation.ProcessArchitecture.ToString());
    public static string Runtime { get; } = Safe(() => RuntimeInformation.FrameworkDescription);

    // The desktop and window system, where the platform says: how windows raise,
    // focus and stack differs between them, and window bugs follow those lines.
    public static string Desktop { get; } = ReadDesktop();

    // Whether this process draws its windows on the graphics card
    // (GlobalSettings.DrawWithGraphicsCard as the file held it at launch). Read
    // straight from global.json: the renderer is chosen before the settings load,
    // and once, so a change made since waits for the next launch.
    public static bool DrawsWithGraphicsCard { get; } = ReadDrawWithGraphicsCard(AppPaths.GlobalSettingsFile);

    public static string Drawing => DrawsWithGraphicsCard
        ? "graphics card (the CPU if the card's driver won't start)"
        : "CPU";

    public static string Build =>
        $"{AppInfo.Version} ({BuildConfiguration}"
        + (Commit is { } commit ? $", commit {commit}" : string.Empty)
        + (BuildTime is { } built ? $", built {built:yyyy-MM-dd HH:mm}" : string.Empty) + ")";

    private static DateTimeOffset ReadProcessStart()
    {
        try
        {
            using Process process = Process.GetCurrentProcess();
            return new DateTimeOffset(process.StartTime);
        }
        catch
        {
            // No process information on this platform: the first read stands in.
            return DateTimeOffset.Now;
        }
    }

    private static string? ReadCommit()
    {
        try
        {
            string? raw = typeof(ClientEnvironment).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            int plus = raw?.IndexOf('+') ?? -1;
            if (raw is null || plus < 0 || plus + 1 >= raw.Length) return null;
            string commit = raw[(plus + 1)..];
            return commit.Length > 12 ? commit[..12] : commit;
        }
        catch { return null; }   // a build with no version attribute has no commit to show
    }

    private static DateTimeOffset? ReadBuildTime()
    {
        try
        {
            // A single-file build has no assembly path, or one unpacked at its first
            // launch, which dates the unpacking: the executable stands in.
            string? path = Update.BundleExtraction.Current() == Update.BundleExtraction.Layout.NotSingleFile
                ? typeof(ClientEnvironment).Assembly.Location : null;
            if (string.IsNullOrEmpty(path)) path = Environment.ProcessPath;
            return string.IsNullOrEmpty(path) || !File.Exists(path)
                ? null
                : new DateTimeOffset(File.GetLastWriteTime(path));
        }
        catch { return null; }   // an unreadable file time is only a missing detail
    }

    private static string ReadDesktop()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows desktop"
                : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macOS desktop" : "(unknown)";
        string desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") is { Length: > 0 } d ? d : "(desktop not named)";
        string session = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") is { Length: > 0 } s ? s
            : Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 } ? "wayland"
            : Environment.GetEnvironmentVariable("DISPLAY") is { Length: > 0 } ? "x11" : "(window system not named)";
        return $"{desktop}, {session}";
    }

    // The setting as global.json holds it; off when the file is missing, won't
    // parse or doesn't say, as a fresh install is.
    internal static bool ReadDrawWithGraphicsCard(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using FileStream file = File.OpenRead(path);
            using JsonDocument doc = JsonDocument.Parse(file, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(nameof(Models.Settings.GlobalSettings.DrawWithGraphicsCard), out JsonElement value)
                && value.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // The settings service reports a bad file once it loads; this launch
            // just draws the default way.
            return false;
        }
    }

    private static string Safe(Func<string> read)
    {
        try { return read(); }
        catch { return "(unknown)"; }   // a platform that won't say is reported as such
    }
}
