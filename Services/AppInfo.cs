using System.Reflection;

namespace FujinTerm.Services;

/// <summary>
/// Compile-time facts about the running app — repo URL, external links, the
/// kind of stuff every Help / About / Report-an-issue menu entry needs.
/// </summary>
public static class AppInfo
{
    public const string DisplayName = "FujinTerm";

    /// <summary>
    /// Version pulled from the compiled assembly's
    /// <c>AssemblyInformationalVersion</c> attribute, which MSBuild
    /// generates from the <c>&lt;Version&gt;</c> property in the
    /// csproj. Single source of truth — bump the csproj, the @version
    /// remote-command reply tracks automatically on the next build.
    /// Falls back to <c>"unknown"</c> in the (impossible) case where
    /// the attribute didn't land.
    /// </summary>
    public static string Version { get; } = ReadAssemblyVersion();

    /// <summary>
    /// <c>"FujinTerm 1.0.0"</c> — the form the <c>@version</c>
    /// remote-command reply uses to match the format other clients
    /// emit (MegaMUD: <c>"MegaMud 1.03u"</c>).
    /// </summary>
    public static string DisplayNameWithVersion { get; } = $"{DisplayName} {Version}";

    public const string RepoUrl    = "https://github.com/Tehshortbus/FujinTerm";
    public const string IssuesUrl  = RepoUrl + "/issues/new";

    public const string MajorMudWikiUrl     = "https://kyau.net/wiki/MajorMUD";
    public const string MajorMudRedditUrl   = "https://www.reddit.com/r/majormud/";
    public const string MudInfoUrl          = "https://www.mudinfo.net/";

    private static string ReadAssemblyVersion()
    {
        Assembly asm = typeof(AppInfo).Assembly;
        // AssemblyInformationalVersion may carry a +commit suffix
        // when SourceLink is enabled — trim it for a clean
        // semver-ish display string.
        string raw = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                  ?? asm.GetName().Version?.ToString(3)
                  ?? "unknown";
        int plus = raw.IndexOf('+');
        return plus >= 0 ? raw[..plus] : raw;
    }
}
