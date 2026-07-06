using System.Reflection;

namespace FujinTerm.Services;

// Compile-time facts about the running app — repo URL, external links, the
// kind of stuff every Help / About / Report-an-issue menu entry needs.
public static class AppInfo
{
    public const string DisplayName = "FujinTerm";

    // Version pulled from the compiled assembly's AssemblyInformationalVersion
    // attribute, which MSBuild generates from the <Version> property in the
    // csproj. Single source of truth — bump the csproj, the @version
    // remote-command reply tracks automatically on the next build. Falls back
    // to "unknown" in the (impossible) case where the attribute didn't land.
    public static string Version { get; } = ReadAssemblyVersion();

    // "FujinTerm 1.0.0" — the form the @version remote-command reply uses to
    // match the format other clients emit (MegaMUD: "MegaMud 1.03u").
    public static string DisplayNameWithVersion { get; } = $"{DisplayName} {Version}";

    public const string RepoUrl    = "https://github.com/Tehshortbus/FujinTerm";
    public const string IssuesUrl  = RepoUrl + "/issues/new";

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
