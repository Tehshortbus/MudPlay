namespace FujinTerm.Services;

/// <summary>
/// Compile-time facts about the running app — repo URL, external links, the
/// kind of stuff every Help / About / Report-an-issue menu entry needs.
/// </summary>
public static class AppInfo
{
    public const string DisplayName = "FujinTerm";

    public const string RepoUrl    = "https://github.com/Tehshortbus/FujinTerm";
    public const string IssuesUrl  = RepoUrl + "/issues/new";

    public const string MajorMudWikiUrl     = "https://kyau.net/wiki/MajorMUD";
    public const string MajorMudRedditUrl   = "https://www.reddit.com/r/majormud/";

    /// <summary>
    /// Best-effort lookup of the dev <c>docs/</c> folder (git-ignored — only
    /// present on a developer machine). Returns <c>null</c> on shipped
    /// builds where the folder isn't bundled.
    /// </summary>
    public static string? TryFindDocsFolder()
    {
        string? dir = AppContext.BaseDirectory;
        for (int hops = 0; hops < 6 && !string.IsNullOrEmpty(dir); hops++)
        {
            string candidate = Path.Combine(dir, "docs");
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}
