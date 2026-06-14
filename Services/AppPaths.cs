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
        BbsDir             = Path.Combine(DataRoot, "BBS");
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
        Directory.CreateDirectory(LogsDir);
    }

    /// <summary>
    /// Per-set Messages catalogue file, scoped INSIDE the game-data
    /// set's folder so the catalogue travels with the set. Replaces
    /// the older <c>Data/Global/Messages/{set}.json</c> location —
    /// pairing the file with the MDB tables keeps a curated realm
    /// together (back it up, copy it to another machine, etc.).
    /// </summary>
    public static string MessagesFile(string setName) =>
        Path.Combine(GameDataSetDir(setName), "messages.json");

    /// <summary>
    /// Per-set Monster Messages catalogue file — one combat-line bundle
    /// per Monsters table row (HitYou / HitOther / DeathLine /
    /// ArmorBlock / Dodge / Miss + flavor prefixes). Sits beside the
    /// per-set spell <see cref="MessagesFile"/> so the realm's complete
    /// parser dataset travels together.
    /// </summary>
    public static string MonsterMessagesFile(string setName) =>
        Path.Combine(GameDataSetDir(setName), "monster-messages.json");

    /// <summary>
    /// User-writable MonsterMessages seed JSON, hosted in the XDG-resolved
    /// <c>Data/Global/</c> folder. Acts as the fallback when the per-set
    /// <see cref="MonsterMessagesFile"/> doesn't exist yet for a set.
    /// Bootstrapped from <see cref="BundledMonsterMessagesSeedFile"/> on
    /// first app launch if missing; the user can hand-edit it (or delete
    /// it to re-bootstrap from the bundled copy).
    /// </summary>
    public static string DefaultMonsterMessagesSeedFile =>
        Path.Combine(DataRoot, "Global", "MonsterMessages.seed.json");

    /// <summary>Read-only bundled copy shipped next to the executable — the bootstrap source.</summary>
    public static string BundledMonsterMessagesSeedFile { get; } =
        Path.Combine(AppContext.BaseDirectory, "Defaults", "MonsterMessages.seed.json");

    /// <summary>
    /// User-writable MonsterOverlay seed JSON for the given realm flavor,
    /// hosted in the XDG-resolved <c>Data/Global/</c> folder. Holds the
    /// Defaults-tier baseline for relationship / priority / NotHostile /
    /// DontBackstab — decoded from the realm's stock MegaMUD
    /// <c>Monsters.md</c>. The active game-data set's <c>Info.json[0].Legit</c>
    /// picks which realm seed to apply (<c>0/1</c> = stock, <c>2</c> =
    /// paradigm). Bootstrapped from the matching
    /// <see cref="BundledMonsterOverlaySeedFile"/> on first app launch.
    /// The seed itself is never written by the app; user edits go to
    /// higher tiers via <see cref="SettingsResolver.WriteGameDataAt"/>.
    /// </summary>
    public static string MonsterOverlaySeedFile(string realm) =>
        Path.Combine(DataRoot, "Global", $"MonsterOverlay.{realm}.seed.json");

    /// <summary>Read-only bundled copy of the realm's overlay seed, shipped next to the executable.</summary>
    public static string BundledMonsterOverlaySeedFile(string realm) =>
        Path.Combine(AppContext.BaseDirectory, "Defaults", $"MonsterOverlay.{realm}.seed.json");

    /// <summary>
    /// User-writable ItemOverlay seed JSON for the given realm flavor —
    /// parallel of <see cref="MonsterOverlaySeedFile"/>, but for items.
    /// Holds the Defaults-tier baseline for the 9 user-facing Options
    /// flags (Auto-collect / Auto-discard / Auto-find / Auto-open /
    /// Auto-buy / Auto-sell / Cannot-be-taken / Must-have-minimum /
    /// Loyal-item) plus MinToKeep / MaxToGet, decoded from the realm's
    /// stock MegaMUD <c>Items.md</c>. The active game-data set's
    /// <c>Info.json[0].Legit</c> picks which realm seed to apply
    /// (<c>0/1</c> = stock, <c>2</c> = paradigm). Bootstrapped from
    /// the matching <see cref="BundledItemOverlaySeedFile"/> on first
    /// app launch. The seed itself is never written by the app; user
    /// edits go to higher tiers via
    /// <see cref="SettingsResolver.WriteGameDataAt"/>.
    /// </summary>
    public static string ItemOverlaySeedFile(string realm) =>
        Path.Combine(DataRoot, "Global", $"ItemOverlay.{realm}.seed.json");

    /// <summary>Read-only bundled copy of the realm's item-overlay seed, shipped next to the executable.</summary>
    public static string BundledItemOverlaySeedFile(string realm) =>
        Path.Combine(AppContext.BaseDirectory, "Defaults", $"ItemOverlay.{realm}.seed.json");

    /// <summary>
    /// Per-set Triggers file scoped inside the game-data set's folder.
    /// Stores only the <see cref="Models.GameData.TriggerLocation.GameData"/>-
    /// scoped triggers; <see cref="Models.GameData.TriggerLocation.Profile"/>-
    /// scoped ones live on <see cref="Models.Profile.CharacterProfile.Triggers"/>.
    /// </summary>
    public static string TriggersFile(string setName) =>
        Path.Combine(GameDataSetDir(setName), "triggers.json");

    /// <summary>
    /// <summary>
    /// User-writable Messages seed JSON, hosted in the XDG-resolved
    /// <c>Data/Global/</c> folder. Shared across every game-data set —
    /// the catalogue's message text (e.g. "You feel lucky") is universal
    /// across MajorMUD realms. <see cref="MessageStore"/> falls back to
    /// this when the user's per-set <see cref="MessagesFile"/> doesn't
    /// exist for the active set. Bootstrapped from
    /// <see cref="BundledMessagesSeedFile"/> on first app launch if
    /// missing; the user can hand-edit it (or delete it to re-bootstrap
    /// from the bundled copy).
    /// </summary>
    public static string DefaultMessagesSeedFile =>
        Path.Combine(DataRoot, "Global", "Messages.seed.json");

    /// <summary>Read-only bundled copy shipped next to the executable — the bootstrap source.</summary>
    public static string BundledMessagesSeedFile { get; } =
        Path.Combine(AppContext.BaseDirectory, "Defaults", "Messages.seed.json");

    /// <summary>
    /// User-writable Triggers seed JSON, hosted in the XDG-resolved
    /// <c>Data/Global/</c> folder. <see cref="TriggerEngine"/> falls
    /// back to this when a set has no per-set <see cref="TriggersFile"/>.
    /// Bootstrapped from <see cref="BundledTriggersSeedFile"/> on first
    /// app launch if missing.
    /// </summary>
    public static string DefaultTriggersSeedFile =>
        Path.Combine(DataRoot, "Global", "Triggers.seed.json");

    /// <summary>Read-only bundled copy shipped next to the executable — the bootstrap source.</summary>
    public static string BundledTriggersSeedFile { get; } =
        Path.Combine(AppContext.BaseDirectory, "Defaults", "Triggers.seed.json");

    /// <summary>
    /// Bootstrap missing seed files in <c>Data/Global/</c> by copying
    /// from the bundled <c>Defaults/</c> next to the executable. Called
    /// once during app startup. Pre-existing user-edited Global seeds
    /// are never overwritten — to reset a seed, delete the Global copy
    /// and the next launch re-bootstraps from the bundled source.
    /// </summary>
    public static void EnsureGlobalSeedsBootstrapped()
    {
        Directory.CreateDirectory(Path.Combine(DataRoot, "Global"));
        TryCopySeed(BundledMessagesSeedFile,        DefaultMessagesSeedFile);
        TryCopySeed(BundledMonsterMessagesSeedFile, DefaultMonsterMessagesSeedFile);
        TryCopySeed(BundledTriggersSeedFile,        DefaultTriggersSeedFile);

        // MonsterOverlay + ItemOverlay seeds are realm-flavored —
        // one file per realm family. The active set picks which to
        // apply via Info.Legit. Bootstrap every realm file we ship so
        // the user can browse / edit any seed without first having to
        // switch active sets.
        foreach (string realm in new[] { "stock", "paradigm" })
        {
            TryCopySeed(BundledMonsterOverlaySeedFile(realm),
                        MonsterOverlaySeedFile(realm));
            TryCopySeed(BundledItemOverlaySeedFile(realm),
                        ItemOverlaySeedFile(realm));
        }
    }

    private static void TryCopySeed(string source, string destination)
    {
        if (File.Exists(destination)) return;
        if (!File.Exists(source)) return;  // dev builds may lack the bundled file — just skip.
        try { File.Copy(source, destination); }
        catch { /* best-effort; if the copy fails the store falls through to an empty seed */ }
    }

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
    /// Per-BBS folder holding BOTH navigation loops AND Auto-Lair
    /// setups. Loops save as <c>{name}.json</c>; lair setups save as
    /// <c>{name}.lair.json</c> — the filename extension is the schema
    /// discriminator so the two managers can scan the same folder and
    /// pick up only their own files. Earlier exports under a separate
    /// <c>Lairs/</c> folder are migrated on first load by
    /// <see cref="Game.Map.LairManager.LoadAll"/>.
    /// </summary>
    public static string BbsLoopsFolder(string bbsName) =>
        Path.Combine(BbsFolder(bbsName), "Loops");

    /// <summary>
    /// Legacy per-BBS folder that held Auto-Lair setups before the
    /// Loops + Lairs storage unification. Kept around as a source for
    /// the one-shot migration in <see cref="Game.Map.LairManager.LoadAll"/>;
    /// once empty, the folder is removed and never recreated.
    /// </summary>
    public static string LegacyBbsLairsFolder(string bbsName) =>
        Path.Combine(BbsFolder(bbsName), "Lairs");

    /// <summary>
    /// Per-BBS observed-players side-file. One PlayerObservation per
    /// player ever seen on this BBS; the Phase 5 PR 5.20 design keeps
    /// observations at the BBS tier so the same display name on a
    /// different BBS counts as a different person.
    /// </summary>
    public static string BbsPlayersFile(string bbsName) =>
        Path.Combine(BbsFolder(bbsName), "players.json");

    /// <summary>
    /// Per-BBS map-room blacklist file. Entries hide their target
    /// rooms from the navigation map render and the search box —
    /// typical use is hiding ganghouse / sysop-only rooms behind
    /// dead-end doors that clutter the layout.
    /// </summary>
    public static string BbsRoomBlacklistFile(string bbsName) =>
        Path.Combine(BbsFolder(bbsName), "room_blacklist.json");

    /// <summary>
    /// Per-BBS folder holding every character that connects to that BBS.
    /// Profiles live UNDER the BBS folder because each MajorMUD server
    /// allows only one character of a given name — so the same character
    /// name on two different BBSes is two different people, and nesting
    /// under the BBS keeps them from colliding on a flat profiles list.
    /// </summary>
    public static string BbsProfilesDir(string bbsName) =>
        Path.Combine(BbsFolder(bbsName), "profiles");

    /// <summary>
    /// Folder holding all files for one character on a given BBS —
    /// primary profile JSON plus per-set override side-files and any
    /// future per-character helper files (macros, triggers, equipment
    /// sets, death history, etc.).
    /// </summary>
    public static string ProfileFolder(string bbsName, string characterName) =>
        Path.Combine(BbsProfilesDir(bbsName), characterName);

    /// <summary>Primary character profile file inside <see cref="ProfileFolder"/>.</summary>
    public static string CharacterProfileFile(string bbsName, string characterName) =>
        Path.Combine(ProfileFolder(bbsName, characterName), "profile.json");

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
    /// <param name="characterBbs">For the Character tier only: the BBS the profile lives under
    /// (profiles nest at <c>BBS/{bbs}/profiles/{char}/</c>). Ignored for other tiers.</param>
    public static string OverrideFile(SettingsTier tier, string? tierScopeName, string table, string setName, string? characterBbs = null)
    {
        string folder = tier switch
        {
            SettingsTier.Defaults  => throw new InvalidOperationException("Defaults tier is read-only — no override side-file."),
            SettingsTier.Global    => Path.Combine(DataRoot, "Global"),
            SettingsTier.Bbs       => BbsFolder(RequireScope(tierScopeName, "BBS")),
            SettingsTier.Character => ProfileFolder(RequireScope(characterBbs, "Character BBS"), RequireScope(tierScopeName, "Character")),
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
