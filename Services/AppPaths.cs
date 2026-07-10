namespace FujinTerm.Services;

// Resolves and exposes every directory and file path FujinTerm reads or writes.
// Centralizes platform-specific conventions (XDG on Linux, %LocalAppData% on
// Windows, ~/Library/Application Support on macOS) so the rest of the app never
// concatenates raw paths.
//
// Everything user-writable sits under a single Data/ root for ease of backup and
// inspection. Setting the FUJINTERM_DATA_ROOT environment variable overrides the
// platform default; useful for tests, portable installs, and sandboxed dev runs.
public static class AppPaths
{
    private const string AppFolderName = "FujinTerm";
    private const string DataSubfolder = "Data";

    // Single root containing all user-writable app data.
    public static string DataRoot { get; }

    // Tiny one-line text file that overrides DataRoot with a user-chosen
    // absolute path. Lives at the platform-config equivalent
    // (Linux: ~/.config/FujinTerm/, Windows: %LocalAppData%\FujinTerm\,
    // macOS: ~/Library/Preferences/FujinTerm/) — the only file FujinTerm writes
    // outside DataRoot. Absent on a fresh install; created by the Settings →
    // General "Change data directory" migration flow. DataRootRelocator writes
    // it; this type only reads it at static-init.
    public static string PointerFile { get; }

    // The path resolution source for the active DataRoot — useful for the
    // Settings UI tooltip.
    public static DataRootSource DataRootResolvedFrom { get; }

    // Imported game-data sets (Defaults tier — read-only base).
    public static string GameDataRoot { get; }

    // Global-tier settings file (one per install).
    public static string GlobalSettingsFile { get; }

    // BBS-tier files (one per BBS).
    public static string BbsDir { get; }

    // Debug logs from DebugLogWriter.
    public static string LogsDir { get; }

    // App-shipped fallback defaults, alongside the executable. Read-only at
    // runtime; populated by the build pipeline.
    public static string DefaultsDir { get; }

    // Production-build starter pack (e.g., the pre-converted v1.11p MDB).
    // Returns the path when present (release artifact); null in dev builds.
    // First-run logic copies this into GameDataRoot when non-null and the
    // game-data root is empty.
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

    // Per-set Messages catalogue file, scoped INSIDE the game-data set's folder
    // so the catalogue travels with the set. Replaces the older
    // Data/Global/Messages/{set}.json location — pairing the file with the MDB
    // tables keeps a curated realm together (back it up, copy it to another
    // machine, etc.).
    public static string MessagesFile(string setName) =>
        Path.Combine(GameDataSetDir(setName), "messages.json");

    // Per-set Monster Messages catalogue file — one combat-line bundle per
    // Monsters table row (HitYou / HitOther / DeathLine / ArmorBlock / Dodge /
    // Miss + flavor prefixes). Sits beside the per-set spell MessagesFile so
    // the realm's complete parser dataset travels together.
    public static string MonsterMessagesFile(string setName) =>
        Path.Combine(GameDataSetDir(setName), "monster-messages.json");

    // User-writable MonsterMessages seed JSON, hosted in the XDG-resolved
    // Data/Global/ folder. Acts as the fallback when the per-set
    // MonsterMessagesFile doesn't exist yet for a set. Bootstrapped from
    // BundledMonsterMessagesSeedFile on first app launch if missing; the user
    // can hand-edit it (or delete it to re-bootstrap from the bundled copy).
    public static string DefaultMonsterMessagesSeedFile =>
        Path.Combine(DataRoot, "Global", "MonsterMessages.seed.json");

    // Read-only bundled copy shipped next to the executable — the bootstrap source.
    public static string BundledMonsterMessagesSeedFile { get; } =
        Path.Combine(AppContext.BaseDirectory, "Defaults", "MonsterMessages.seed.json");

    // User-writable MonsterOverlay seed JSON for the given realm flavor, hosted
    // in the XDG-resolved Data/Global/ folder. Holds the Defaults-tier baseline
    // for relationship / priority / DontBackstab — decoded from
    // the realm's stock MegaMUD Monsters.md. The active game-data set's
    // Info.json[0].Legit picks which realm seed to apply (0/1 = stock, 2 =
    // paradigm). Bootstrapped from the matching BundledMonsterOverlaySeedFile
    // on first app launch. The seed itself is never written by the app; user
    // edits go to higher tiers via SettingsResolver.WriteGameDataAt.
    public static string MonsterOverlaySeedFile(string realm) =>
        Path.Combine(DataRoot, "Global", $"MonsterOverlay.{realm}.seed.json");

    // Read-only bundled copy of the realm's overlay seed, shipped next to the executable.
    public static string BundledMonsterOverlaySeedFile(string realm) =>
        Path.Combine(AppContext.BaseDirectory, "Defaults", $"MonsterOverlay.{realm}.seed.json");

    // User-writable ItemOverlay seed JSON for the given realm flavor — parallel
    // of MonsterOverlaySeedFile, but for items. Holds the Defaults-tier
    // baseline for the 9 user-facing Options flags (Auto-collect / Auto-discard
    // / Auto-find / Auto-open / Auto-buy / Auto-sell / Cannot-be-taken /
    // Must-have-minimum / Loyal-item) plus MinToKeep / MaxToGet, decoded from
    // the realm's stock MegaMUD Items.md. The active game-data set's
    // Info.json[0].Legit picks which realm seed to apply (0/1 = stock, 2 =
    // paradigm). Bootstrapped from the matching BundledItemOverlaySeedFile on
    // first app launch. The seed itself is never written by the app; user edits
    // go to higher tiers via SettingsResolver.WriteGameDataAt.
    public static string ItemOverlaySeedFile(string realm) =>
        Path.Combine(DataRoot, "Global", $"ItemOverlay.{realm}.seed.json");

    // Read-only bundled copy of the realm's item-overlay seed, shipped next to the executable.
    public static string BundledItemOverlaySeedFile(string realm) =>
        Path.Combine(AppContext.BaseDirectory, "Defaults", $"ItemOverlay.{realm}.seed.json");

    // Per-set Triggers file scoped inside the game-data set's folder. Stores
    // only the TriggerLocation.GameData-scoped triggers; the
    // TriggerLocation.Profile-scoped ones live on CharacterProfile.Triggers.
    public static string TriggersFile(string setName) =>
        Path.Combine(GameDataSetDir(setName), "triggers.json");

    // Per-set Quest definitions overlay scoped inside the game-data set's folder
    // (sibling to TriggersFile). Holds the user-owned quest layer — display
    // name, show/hide visibility, and edited step markdown, keyed by quest-flag
    // number + step. QuestStore resolves it over the universal
    // DefaultQuestDefsSeedFile underlay; the mechanical data (ordered steps +
    // stat bonuses) is crawled from the set's TBInfo at runtime, not stored here.
    public static string QuestsFile(string setName) =>
        Path.Combine(GameDataSetDir(setName), "quests.json");

    // User-writable Messages seed JSON, hosted in the XDG-resolved Data/Global/
    // folder. Shared across every game-data set — the catalogue's message text
    // (e.g. "You feel lucky") is universal across MajorMUD realms. MessageStore
    // falls back to this when the user's per-set MessagesFile doesn't exist for
    // the active set. Bootstrapped from BundledMessagesSeedFile on first app
    // launch if missing; the user can hand-edit it (or delete it to re-bootstrap
    // from the bundled copy).
    public static string DefaultMessagesSeedFile =>
        Path.Combine(DataRoot, "Global", "Messages.seed.json");

    // Read-only bundled copy shipped next to the executable — the bootstrap source.
    public static string BundledMessagesSeedFile { get; } =
        Path.Combine(AppContext.BaseDirectory, "Defaults", "Messages.seed.json");

    // User-writable Triggers seed JSON, hosted in the XDG-resolved Data/Global/
    // folder. TriggerEngine falls back to this when a set has no per-set
    // TriggersFile. Bootstrapped from BundledTriggersSeedFile on first app
    // launch if missing.
    public static string DefaultTriggersSeedFile =>
        Path.Combine(DataRoot, "Global", "Triggers.seed.json");

    // Read-only bundled copy shipped next to the executable — the bootstrap source.
    public static string BundledTriggersSeedFile { get; } =
        Path.Combine(AppContext.BaseDirectory, "Defaults", "Triggers.seed.json");

    // User-writable Quest definitions seed JSON, hosted in the XDG-resolved
    // Data/Global/ folder. Universal across every game-data set — keyed by
    // quest-flag number + step, which custom realms reuse for the same quests,
    // so a curated set of names + step write-ups ports everywhere. QuestStore
    // falls back to this when the active set's per-set QuestsFile doesn't name a
    // quest. Bootstrapped from BundledQuestDefsSeedFile on first app launch if
    // missing; never written by the app (user edits go to the per-set overlay).
    public static string DefaultQuestDefsSeedFile =>
        Path.Combine(DataRoot, "Global", "QuestDefs.seed.json");

    // Read-only bundled copy shipped next to the executable — the bootstrap source.
    public static string BundledQuestDefsSeedFile { get; } =
        Path.Combine(AppContext.BaseDirectory, "Defaults", "QuestDefs.seed.json");

    // Bootstrap missing seed files in Data/Global/ by copying from the bundled
    // Defaults/ next to the executable. Called once during app startup.
    // Pre-existing user-edited Global seeds are never overwritten — to reset a
    // seed, delete the Global copy and the next launch re-bootstraps from the
    // bundled source.
    public static void EnsureGlobalSeedsBootstrapped()
    {
        Directory.CreateDirectory(Path.Combine(DataRoot, "Global"));
        TryCopySeed(BundledMessagesSeedFile,        DefaultMessagesSeedFile);
        TryCopySeed(BundledMonsterMessagesSeedFile, DefaultMonsterMessagesSeedFile);
        TryCopySeed(BundledTriggersSeedFile,        DefaultTriggersSeedFile);
        TryCopySeed(BundledQuestDefsSeedFile,       DefaultQuestDefsSeedFile);

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

    // Path to a single imported game-data set's directory.
    public static string GameDataSetDir(string setName) =>
        Path.Combine(GameDataRoot, setName);

    // Folder holding all files for one BBS — primary settings JSON plus per-set
    // override side-files (monster_overrides.{set}.json,
    // message_overrides.{set}.json, …) and any future helper files (per-BBS
    // favorites list, character roster, etc.).
    public static string BbsFolder(string bbsName) =>
        Path.Combine(BbsDir, bbsName);

    // Primary BBS settings file inside BbsFolder.
    public static string BbsProfileFile(string bbsName) =>
        Path.Combine(BbsFolder(bbsName), "bbs.json");

    // Per-game-data-set folder holding the whole navigation library: loops
    // ({name}.loop), Auto-Lair setups ({name}.lair), and the user-created
    // sub-folder tree that organises both. The filename suffix is the schema
    // discriminator so the loop and lair managers can scan the same folder and
    // pick up only their own files. Keyed on the game-data set (the realm's MDB)
    // rather than the BBS, so the same nav library follows the realm across
    // every BBS / character that points at that set.
    public static string GameDataSetLoopsFolder(string setName) =>
        Path.Combine(GameDataSetDir(setName), "Loops");

    // Legacy per-BBS folder that held Auto-Lair setups before the Loops + Lairs
    // storage unification. Kept around as a source for the one-shot migration in
    // Game.Map.LairManager.LoadAll; once empty, the folder is removed and never
    // recreated.
    public static string LegacyBbsLairsFolder(string bbsName) =>
        Path.Combine(BbsFolder(bbsName), "Lairs");

    // Per-BBS observed-players side-file. One PlayerObservation per player ever
    // seen on this BBS; observations live at the BBS tier so the same display
    // name on a different BBS counts as a different person.
    public static string BbsPlayersFile(string bbsName) =>
        Path.Combine(BbsFolder(bbsName), "players.json");

    // Per-BBS map-room blacklist file. Entries hide their target rooms from the
    // navigation map render and the search box — typical use is hiding
    // ganghouse / sysop-only rooms behind dead-end doors that clutter the layout.
    public static string BbsRoomBlacklistFile(string bbsName) =>
        Path.Combine(BbsFolder(bbsName), "room_blacklist.json");

    // Per-BBS folder holding every character that connects to that BBS. Profiles
    // live UNDER the BBS folder because each MajorMUD server allows only one
    // character of a given name — so the same character name on two different
    // BBSes is two different people, and nesting under the BBS keeps them from
    // colliding on a flat profiles list.
    public static string BbsProfilesDir(string bbsName) =>
        Path.Combine(BbsFolder(bbsName), "profiles");

    // Folder holding all files for one character on a given BBS — primary
    // profile JSON plus per-set override side-files and any future per-character
    // helper files (macros, triggers, equipment sets, death history, etc.).
    public static string ProfileFolder(string bbsName, string characterName) =>
        Path.Combine(BbsProfilesDir(bbsName), characterName);

    // Primary character profile file inside ProfileFolder.
    public static string CharacterProfileFile(string bbsName, string characterName) =>
        Path.Combine(ProfileFolder(bbsName, characterName), "profile.json");

    // Per-set game-data override side-file at the given tier. Routes to the
    // right folder: Global → DataRoot/Global, BBS → BbsFolder,
    // Character → ProfileFolder. File name is
    // {table-lowercase}_overrides.{set}.json, e.g.
    // monster_overrides.data-v1.11p.json. The Defaults tier is read-only and
    // throws. tierScopeName is the BBS or profile name for BBS / Character
    // tiers (ignored for Global). characterBbs, for the Character tier only, is
    // the BBS the profile lives under (profiles nest at
    // BBS/{bbs}/profiles/{char}/); ignored for other tiers.
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

    // Path for a new debug log file. Caller supplies a topic; the timestamp is
    // generated at call time so concurrent loggers don't collide.
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

// Where AppPaths.DataRoot was resolved from at startup.
public enum DataRootSource
{
    // Platform-standard user-data path (the install default).
    PlatformDefault,

    // User-relocated; AppPaths.PointerFile points here.
    PointerFile,

    // FUJINTERM_DATA_ROOT env var override (tests / CI).
    EnvironmentVariable,
}
