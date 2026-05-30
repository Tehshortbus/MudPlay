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

    /// <summary>
    /// Tiny one-line text file that overrides <see cref="DataRoot"/> with a
    /// user-chosen absolute path. Lives at the platform-config equivalent
    /// (Linux: <c>~/.config/FujinTerm/</c>, Windows: <c>%LocalAppData%\FujinTerm\</c>,
    /// macOS: <c>~/Library/Preferences/FujinTerm/</c>) — the *only* file
    /// FujinTerm writes outside <see cref="DataRoot"/>. Absent on a fresh
    /// install; created by the Settings → General "Change data directory"
    /// migration flow. <see cref="DataRootRelocator"/> writes it; this
    /// type only reads it at static-init.
    /// </summary>
    public static string PointerFile { get; }

    /// <summary>The path resolution source for the active <see cref="DataRoot"/> — useful for the Settings UI tooltip.</summary>
    public static DataRootSource DataRootResolvedFrom { get; }

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
    /// Per-game-data-set Messages/Responses catalogues, one JSON file
    /// per imported set (paired with the folder name under
    /// <see cref="GameDataRoot"/>).
    /// </summary>
    public static string MessagesDir { get; }

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
        // Pointer file lives at the platform's "config" location — separate from
        // the data root itself so the user can relocate data to a different
        // drive without losing the breadcrumb that tells us where it went.
        //   Linux  → $XDG_CONFIG_HOME (or ~/.config)
        //   Win    → %LOCALAPPDATA%   (Windows doesn't separate config / data)
        //   macOS  → ~/Library/Preferences
        string configDir = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolderOption.Create);
        PointerFile = Path.Combine(configDir, AppFolderName, "data-location.txt");

        // Resolution order: env var → pointer file → platform default.
        // FUJINTERM_DATA_ROOT wins for tests and CI; pointer file wins for
        // user-relocated installs; otherwise the OS standard data location.
        string? envOverride = Environment.GetEnvironmentVariable("FUJINTERM_DATA_ROOT");
        if (!string.IsNullOrWhiteSpace(envOverride))
        {
            DataRoot             = Path.GetFullPath(envOverride);
            DataRootResolvedFrom = DataRootSource.EnvironmentVariable;
        }
        else if (TryReadPointerFile(PointerFile, out string? pointed))
        {
            DataRoot             = pointed;
            DataRootResolvedFrom = DataRootSource.PointerFile;
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
            DataRoot             = Path.Combine(baseDir, AppFolderName, DataSubfolder);
            DataRootResolvedFrom = DataRootSource.PlatformDefault;
        }

        GameDataRoot       = Path.Combine(DataRoot, "game data");
        GlobalSettingsFile = Path.Combine(DataRoot, "Global", "global.json");
        MessagesDir        = Path.Combine(DataRoot, "Global", "Messages");
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
        Directory.CreateDirectory(MessagesDir);
        Directory.CreateDirectory(BbsDir);
        Directory.CreateDirectory(ProfilesDir);
        Directory.CreateDirectory(LogsDir);
    }

    /// <summary>Path to a single set's Messages/Responses JSON.</summary>
    public static string MessagesFile(string setName) =>
        Path.Combine(MessagesDir, setName + ".json");

    /// <summary>Path to a single imported game-data set's directory.</summary>
    public static string GameDataSetDir(string setName) =>
        Path.Combine(GameDataRoot, setName);

    /// <summary>
    /// Folder holding all files for one BBS — primary settings JSON
    /// plus per-set override side-files (<c>monster_overrides.{set}.json</c>,
    /// <c>message_overrides.{set}.json</c>, …) and any future helper
    /// files (per-BBS favorites list, character roster, etc.).
    /// </summary>
    public static string BbsFolder(string bbsName) =>
        Path.Combine(BbsDir, bbsName);

    /// <summary>Primary BBS settings file inside <see cref="BbsFolder"/>.</summary>
    public static string BbsProfileFile(string bbsName) =>
        Path.Combine(BbsFolder(bbsName), "bbs.json");

    /// <summary>
    /// Folder holding all files for one character — primary profile
    /// JSON plus per-set override side-files and any future
    /// per-character helper files (macros, triggers, equipment sets,
    /// death history, etc.).
    /// </summary>
    public static string ProfileFolder(string characterName) =>
        Path.Combine(ProfilesDir, characterName);

    /// <summary>Primary character profile file inside <see cref="ProfileFolder"/>.</summary>
    public static string CharacterProfileFile(string characterName) =>
        Path.Combine(ProfileFolder(characterName), "profile.json");

    /// <summary>
    /// Per-set game-data override side-file at the given tier. Routes
    /// to the right folder: Global → <see cref="DataRoot"/>/Global,
    /// BBS → <see cref="BbsFolder"/>, Character → <see cref="ProfileFolder"/>.
    /// File name is <c>{table-lowercase}_overrides.{set}.json</c>, e.g.
    /// <c>monster_overrides.data-v1.11p.json</c>. <see cref="SettingsTier.Defaults"/>
    /// is read-only and throws.
    /// </summary>
    /// <param name="tier">Tier the override lives at.</param>
    /// <param name="tierScopeName">For BBS / Character tiers: the BBS or profile name. Ignored for Global.</param>
    /// <param name="table">Game-data table the override applies to (e.g. <c>"Monsters"</c>, <c>"Messages"</c>).</param>
    /// <param name="setName">Active game-data set name (paired with the override file).</param>
    public static string OverrideFile(SettingsTier tier, string? tierScopeName, string table, string setName)
    {
        string folder = tier switch
        {
            SettingsTier.Defaults  => throw new InvalidOperationException("Defaults tier is read-only — no override side-file."),
            SettingsTier.Global    => Path.Combine(DataRoot, "Global"),
            SettingsTier.Bbs       => BbsFolder(RequireScope(tierScopeName, "BBS")),
            SettingsTier.Character => ProfileFolder(RequireScope(tierScopeName, "Character")),
            _ => throw new ArgumentOutOfRangeException(nameof(tier)),
        };
        return Path.Combine(folder, $"{table.ToLowerInvariant()}_overrides.{setName}.json");
    }

    private static string RequireScope(string? scope, string label)
        => string.IsNullOrWhiteSpace(scope)
            ? throw new InvalidOperationException($"OverrideFile for {label} tier requires a scope name (the BBS or profile).")
            : scope;

    /// <summary>
    /// Path for a new debug log file. Caller supplies a topic; the timestamp is
    /// generated at call time so concurrent loggers don't collide.
    /// </summary>
    public static string NewDebugLogFile(string topic)
    {
        string ts = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        return Path.Combine(LogsDir, $"{ts}-{topic}.log");
    }

    private static bool TryReadPointerFile(string path, out string resolved)
    {
        resolved = string.Empty;
        try
        {
            if (!File.Exists(path)) return false;
            string line = File.ReadAllText(path).Trim();
            if (line.Length == 0) return false;

            // Defensive: only accept a real, absolute, syntactically-valid path.
            // A stale or hand-edited pointer to a non-existent path is still
            // honoured (we'll create the structure there) — but garbage that
            // isn't even a valid path is silently ignored.
            string full = Path.GetFullPath(line);
            resolved = full;
            return true;
        }
        catch
        {
            // Unreadable / permission-denied / malformed → silently fall back
            // to the platform default. The Settings UI is the place to fix it,
            // not the bootstrap.
            return false;
        }
    }
}

/// <summary>Where <see cref="AppPaths.DataRoot"/> was resolved from at startup.</summary>
public enum DataRootSource
{
    /// <summary>Platform-standard user-data path (the install default).</summary>
    PlatformDefault,

    /// <summary>User-relocated; <see cref="AppPaths.PointerFile"/> points here.</summary>
    PointerFile,

    /// <summary><c>FUJINTERM_DATA_ROOT</c> env var override (tests / CI).</summary>
    EnvironmentVariable,
}
