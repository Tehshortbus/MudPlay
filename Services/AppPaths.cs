namespace FujinTerm.Services;

/// <summary>
/// Resolves and exposes every directory and file path FujinTerm reads or writes.
/// Centralizes platform-specific conventions (XDG on Linux, %LocalAppData% on
/// Windows, ~/Library/Application Support on macOS) so the rest of the app never
/// concatenates raw paths.
/// </summary>
/// <remarks>
/// Everything user-writable sits under a single <c>Data/</c> root for ease of
/// backup and inspection — see <c>docs/00-foundations.md</c> for the layout.
/// Setting the <c>FUJINTERM_DATA_ROOT</c> environment variable overrides the
/// platform default; useful for tests, portable installs, and sandboxed dev runs.
/// </remarks>
public static class AppPaths
{
    private const string AppFolderName = "FujinTerm";
    private const string DataSubfolder = "Data";

    /// <summary>Single root containing all user-writable app data.</summary>
    public static string DataRoot { get; }

    /// <summary>Imported game-data sets (Defaults tier — read-only base).</summary>
    public static string GameDataRoot { get; }

    /// <summary>Global-tier settings file (one per install).</summary>
    public static string GlobalSettingsFile { get; }

    /// <summary>BBS-tier files (one per BBS).</summary>
    public static string BbsDir { get; }

    /// <summary>Character profiles (one per character).</summary>
    public static string ProfilesDir { get; }

    /// <summary>Debug logs from DebugLogWriter.</summary>
    public static string LogsDir { get; }

    /// <summary>
    /// App-shipped fallback defaults, alongside the executable.
    /// Read-only at runtime; populated by the build pipeline.
    /// </summary>
    public static string DefaultsDir { get; }

    /// <summary>
    /// Production-build starter pack (e.g., the pre-converted v1.11p MDB).
    /// Returns the path when present (release artifact); <c>null</c> in dev builds.
    /// First-run logic in later phases copies this into <see cref="GameDataRoot"/>
    /// when non-null and the game-data root is empty.
    /// </summary>
    public static string? BundledDataDir { get; }

    static AppPaths()
    {
        // FUJINTERM_DATA_ROOT lets tests and portable installs relocate the entire
        // Data tree to an arbitrary directory.
        string? envOverride = Environment.GetEnvironmentVariable("FUJINTERM_DATA_ROOT");
        if (!string.IsNullOrWhiteSpace(envOverride))
        {
            DataRoot = Path.GetFullPath(envOverride);
        }
        else
        {
            // LocalApplicationData maps cleanly across platforms:
            //   Linux  → $XDG_DATA_HOME (or ~/.local/share)
            //   Win    → %LOCALAPPDATA%
            //   macOS  → ~/Library/Application Support
            string baseDir = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.Create);
            DataRoot = Path.Combine(baseDir, AppFolderName, DataSubfolder);
        }

        GameDataRoot       = Path.Combine(DataRoot, "game data");
        GlobalSettingsFile = Path.Combine(DataRoot, "Global", "global.json");
        BbsDir             = Path.Combine(DataRoot, "BBS");
        ProfilesDir        = Path.Combine(DataRoot, "profiles");
        LogsDir            = Path.Combine(DataRoot, "Logs");

        string exeDir = AppContext.BaseDirectory;
        DefaultsDir = Path.Combine(exeDir, "Defaults");

        // bundled-data ships with release artifacts only; absence == dev build.
        string candidate = Path.Combine(exeDir, "bundled-data");
        BundledDataDir = Directory.Exists(candidate) ? candidate : null;

        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(GameDataRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(GlobalSettingsFile)!);
        Directory.CreateDirectory(BbsDir);
        Directory.CreateDirectory(ProfilesDir);
        Directory.CreateDirectory(LogsDir);
    }

    /// <summary>Path to a single imported game-data set's directory.</summary>
    public static string GameDataSetDir(string setName) =>
        Path.Combine(GameDataRoot, setName);

    /// <summary>Path to a single BBS profile file.</summary>
    public static string BbsProfileFile(string bbsName) =>
        Path.Combine(BbsDir, bbsName + ".json");

    /// <summary>Path to a single character profile file.</summary>
    public static string CharacterProfileFile(string characterName) =>
        Path.Combine(ProfilesDir, characterName + ".json");

    /// <summary>
    /// Path for a new debug log file. Caller supplies a topic; the timestamp is
    /// generated at call time so concurrent loggers don't collide.
    /// </summary>
    public static string NewDebugLogFile(string topic)
    {
        string ts = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        return Path.Combine(LogsDir, $"{ts}-{topic}.log");
    }
}
