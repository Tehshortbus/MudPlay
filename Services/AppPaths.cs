namespace MudPlay.Services;

// Resolves and exposes every directory and file path MudPlay reads or writes.
// Centralizes platform-specific conventions (XDG on Linux, %LocalAppData% on
// Windows, ~/Library/Application Support on macOS) so the rest of the app never
// concatenates raw paths.
//
// Everything user-writable sits under a single data root — the app folder itself
// (e.g. ~/.local/share/MudPlay/) — for ease of backup and inspection. Setting the
// MUDPLAY_DATA_ROOT environment variable overrides the platform default; useful
// for tests, portable installs, and sandboxed dev runs.
public static class AppPaths
{
    private const string AppFolderName = "MudPlay";
    // Pre-3.0 folder name. On first 3.x launch we migrate a user's data out of
    // the old "FujinTerm" folders into the new MudPlay ones (non-destructive,
    // one-time), so updating from a 2.x build doesn't appear to lose profiles /
    // settings / BBS data. See MigrateLegacyData.
    private const string LegacyAppFolderName = "FujinTerm";
    // Older installs nested everything under an extra "Data/" level (<app>/Data/…).
    // We now use the app folder itself as the data root and lift that subfolder's
    // contents up on first launch (see FlattenDataSubfolder). The name is still
    // needed to locate the old subfolder — both the earlier MudPlay one and the
    // pre-3.0 FujinTerm one.
    private const string DataSubfolder = "Data";

    // One-line summary of any legacy migration performed at static-init, for the
    // program log (AppServices reads it once services + logging exist). null when
    // nothing was migrated.
    public static string? MigrationNote { get; private set; }

    // Single root containing all user-writable app data.
    public static string DataRoot { get; }

    // Tiny one-line text file that overrides DataRoot with a user-chosen
    // absolute path. Lives at the platform-config equivalent
    // (Linux: ~/.config/MudPlay/, Windows: %LocalAppData%\MudPlay\,
    // macOS: ~/Library/Preferences/MudPlay/) — the only file MudPlay writes
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

    // The "default profile" — a full CharacterProfile persisted in the Global
    // folder, loaded on startup / File → New when no named character is chosen.
    // Its settings are the install-wide defaults (what loads before any profile),
    // and File → Save As copies it into named profiles, so a new character starts
    // from these defaults. Distinct from GlobalSettingsFile (the settings tier).
    public static string DefaultProfileFile { get; }

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
        // LocalApplicationData maps cleanly across platforms:
        //   Linux  → $XDG_DATA_HOME (or ~/.local/share)
        //   Win    → %LOCALAPPDATA%
        //   macOS  → ~/Library/Application Support
        string baseDir = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);

        // Carry the data-location pointer over first (relocated installs), so the
        // resolution below reads the MudPlay pointer that used to be FujinTerm's.
        MigrateLegacyDir(Path.Combine(configDir, LegacyAppFolderName),
                         Path.Combine(configDir, AppFolderName));
        PointerFile = Path.Combine(configDir, AppFolderName, "data-location.txt");

        // Resolution order: env var → pointer file → platform default.
        // MUDPLAY_DATA_ROOT wins for tests and CI; pointer file wins for
        // user-relocated installs; otherwise the OS standard data location.
        string? envOverride = Environment.GetEnvironmentVariable("MUDPLAY_DATA_ROOT");
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
            // The app folder itself is the data root now — no nested Data/ level.
            DataRoot             = Path.Combine(baseDir, AppFolderName);
            DataRootResolvedFrom = DataRootSource.PlatformDefault;
        }

        // Default-location migrations, oldest layout last so each backfills what the
        // previous didn't cover. Relocated installs (env / pointer) already point at
        // the user's real, already-flat data, so nothing here runs for them.
        if (DataRootResolvedFrom == DataRootSource.PlatformDefault)
        {
            // Older installs stored everything one level down in <app>/Data/. Lift
            // it up into the app folder and drop the now-redundant Data/ level.
            FlattenDataSubfolder(Path.Combine(DataRoot, DataSubfolder), DataRoot);
            // Pre-3.0 "FujinTerm" installs: backfill from the old FujinTerm/Data
            // folder (non-destructive, one-time; now lands flat). See MigrateLegacyData.
            MigrateLegacyData(Path.Combine(baseDir, LegacyAppFolderName, DataSubfolder), DataRoot);
        }

        GameDataRoot       = Path.Combine(DataRoot, "game data");
        GlobalSettingsFile = Path.Combine(DataRoot, "Global", "global.json");
        DefaultProfileFile = Path.Combine(DataRoot, "Global", "default-profile.json");
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

    // One-time lift of an older install's data out of the nested "Data/" subfolder
    // (the data root used to be <app>/Data) up into the app folder itself, then
    // removes the emptied Data/. Runs only on the platform-default root; relocated
    // installs (env / pointer) already point straight at the user's flat data.
    //
    // Fast path renames each top-level entry up a level (a same-volume move — near
    // instant, no copy); a destination that already exists (a prior partial run, or
    // the empty dirs the ctor pre-creates) falls back to a non-destructive
    // copy-merge so newer flat data is never clobbered. Best-effort: on any error
    // the Data/ folder is left intact and the next launch retries — the app reads
    // nothing until the lift completes, exactly like the FujinTerm migration below.
    internal static void FlattenDataSubfolder(string dataSubfolder, string dataRoot)
    {
        try
        {
            if (!Directory.Exists(dataSubfolder)) return;   // fresh install / already flattened

            foreach (string dir in Directory.GetDirectories(dataSubfolder))
            {
                string dest = Path.Combine(dataRoot, Path.GetFileName(dir));
                if (Directory.Exists(dest))
                {
                    CopyMissing(dir, dest);
                    Directory.Delete(dir, recursive: true);
                }
                else
                {
                    Directory.Move(dir, dest);
                }
            }
            foreach (string file in Directory.GetFiles(dataSubfolder))
            {
                string dest = Path.Combine(dataRoot, Path.GetFileName(file));
                if (File.Exists(dest)) File.Delete(file);   // a newer flat copy already won
                else File.Move(file, dest);
            }

            Directory.Delete(dataSubfolder, recursive: true);   // now empty
            MigrationNote = $"Moved your data out of the old '{DataSubfolder}' subfolder " +
                $"up into the '{AppFolderName}' folder.";
        }
        catch
        {
            // Permission / disk / lock error — leave Data/ intact so the next launch
            // retries; nothing is deleted before its contents are safely relocated.
        }
    }

    // Non-destructive, one-time migration of a pre-3.0 "FujinTerm" data root into
    // the new MudPlay one. Marker-gated so it runs at most once; copies only files
    // the new root LACKS (never overwrites), so a user's profiles / BBS folders /
    // global settings / imported game data are backfilled without clobbering
    // anything MudPlay already created. Best-effort: any failure leaves the legacy
    // folder intact and unmarked, so the next launch retries.
    private static void MigrateLegacyData(string legacyRoot, string newRoot)
    {
        try
        {
            if (!Directory.Exists(legacyRoot)) return;
            string marker = Path.Combine(newRoot, ".migrated-from-fujinterm");
            if (File.Exists(marker)) return;

            int copied = CopyMissing(legacyRoot, newRoot);
            Directory.CreateDirectory(newRoot);
            File.WriteAllText(marker, $"migrated {copied} file(s) from {legacyRoot}");
            if (copied > 0)
                MigrationNote = $"Migrated {copied} file(s) from the pre-3.0 " +
                    $"'{LegacyAppFolderName}' data folder into '{AppFolderName}'.";
        }
        catch
        {
            // Permissions / disk error — don't block startup. Legacy data is left
            // untouched and unmarked, so a later launch tries again.
        }
    }

    // Config-dir counterpart — carries the data-location pointer (relocated
    // installs) from the old FujinTerm config folder to the MudPlay one.
    private static void MigrateLegacyDir(string legacyDir, string newDir)
    {
        try
        {
            if (!Directory.Exists(legacyDir)) return;
            string marker = Path.Combine(newDir, ".migrated-from-fujinterm");
            if (File.Exists(marker)) return;
            CopyMissing(legacyDir, newDir);
            Directory.CreateDirectory(newDir);
            File.WriteAllText(marker, $"migrated from {legacyDir}");
        }
        catch { /* best-effort — see MigrateLegacyData */ }
    }

    // Recursively copy every file under src that dst doesn't already have; files
    // already in dst are left untouched. Returns how many files were copied.
    // internal so the migration's non-destructive contract can be unit-tested.
    internal static int CopyMissing(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        int copied = 0;
        foreach (string dir in Directory.GetDirectories(src))
            copied += CopyMissing(dir, Path.Combine(dst, Path.GetFileName(dir)));
        foreach (string file in Directory.GetFiles(src))
        {
            string target = Path.Combine(dst, Path.GetFileName(file));
            if (File.Exists(target)) continue;
            File.Copy(file, target);
            copied++;
        }
        return copied;
    }

    // Per-set Messages catalogue file, scoped INSIDE the game-data set's folder
    // so the catalogue travels with the set. Replaces the older
    // Global/Messages/{set}.json location — pairing the file with the MDB
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

    // Per-set staged message candidates — raw wire lines MessageCandidateWatcher
    // captured because they matched no MessagesFile record and no registered
    // MessageRouter pattern. Pure runtime-observed state, not curated data, so
    // unlike MessagesFile there is no seed-file fallback.
    public static string MessageCandidatesFile(string setName) =>
        Path.Combine(GameDataSetDir(setName), "message-candidates.json");

    // Per-set editable flavor-prefix vocabulary — the adjectives the game prepends
    // to a monster's base name ("large", "nasty", …). Sits beside the other per-set
    // parser data so the realm's vocabulary travels with it. No seed file: absent
    // this file the built-in MonsterFlavorPrefixes.DefaultPrefixes apply, and the
    // Game Data Browser writes the whole current list here once the user customizes.
    public static string FlavorPrefixesFile(string setName) =>
        Path.Combine(GameDataSetDir(setName), "flavor-prefixes.json");

    // User-writable MonsterMessages seed JSON, hosted in the XDG-resolved
    // Global/ folder. Acts as the fallback when the per-set
    // MonsterMessagesFile doesn't exist yet for a set. Bootstrapped from
    // BundledMonsterMessagesSeedFile on first app launch if missing; the user
    // can hand-edit it (or delete it to re-bootstrap from the bundled copy).
    public static string DefaultMonsterMessagesSeedFile =>
        Path.Combine(DataRoot, "Global", "MonsterMessages.seed.json");

    // Read-only bundled copy shipped next to the executable — the bootstrap source.
    public static string BundledMonsterMessagesSeedFile { get; } =
        Path.Combine(AppContext.BaseDirectory, "Defaults", "MonsterMessages.seed.json");

    // User-writable MonsterOverlay seed JSON for the given realm flavor, hosted
    // in the XDG-resolved Global/ folder. Holds the Defaults-tier baseline
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
    // The user's quest-definition overlay, hosted at the BBS tier
    // (BBS/{bbs}/quests.json). QuestStore resolves it ABOVE the universal
    // DefaultQuestDefsSeedFile underlay, so a player's edits belong to the board
    // they're playing, not the imported game-data set. The mechanical data
    // (ordered steps + stat bonuses) is still crawled from the active set's TBInfo
    // at runtime, not stored here.
    public static string QuestsFileForBbs(string bbsName) =>
        Path.Combine(BbsFolder(bbsName), "quests.json");

    // User-writable Messages seed JSON for the given realm flavor, hosted in the
    // XDG-resolved Global/ folder. Realm-flavored (stock / paradigm), each decoded
    // from that realm's MegaMUD messages.md — the active set's Info.json[0].Legit
    // picks which to apply (0/1 = stock, 2 = paradigm) via GameDataRealm.Resolve.
    // MessageStore falls back to this when the per-set MessagesFile doesn't exist.
    // Bootstrapped from the matching BundledMessagesSeedFile on first launch (or
    // delete the Global copy to re-bootstrap from the bundled source).
    public static string MessagesSeedFile(string realm) =>
        Path.Combine(DataRoot, "Global", $"Messages.{realm}.seed.json");

    // Read-only bundled copy of the realm's message seed, shipped next to the executable.
    public static string BundledMessagesSeedFile(string realm) =>
        Path.Combine(AppContext.BaseDirectory, "Defaults", $"Messages.{realm}.seed.json");

    // The pre-split single universal Messages seed in Global/. Retained only so the
    // one-time migration can detect and retire an existing user's stale copy — nothing
    // reads it for message content anymore now that the seed is realm-flavored.
    public static string LegacyMessagesSeedFile =>
        Path.Combine(DataRoot, "Global", "Messages.seed.json");

    // User-writable Triggers seed JSON, hosted in the XDG-resolved Global/
    // folder. TriggerEngine falls back to this when a set has no per-set
    // TriggersFile. Bootstrapped from BundledTriggersSeedFile on first app
    // launch if missing.
    public static string DefaultTriggersSeedFile =>
        Path.Combine(DataRoot, "Global", "Triggers.seed.json");

    // Read-only bundled copy shipped next to the executable — the bootstrap source.
    public static string BundledTriggersSeedFile { get; } =
        Path.Combine(AppContext.BaseDirectory, "Defaults", "Triggers.seed.json");

    // User-writable Quest definitions seed JSON, hosted in the XDG-resolved
    // Global/ folder. Universal across every game-data set — keyed by
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

    // Per-set boss-catalog overlay scoped inside the game-data set's folder — the
    // user-owned boss layer (added/removed bosses, edited rooms, stop-before flags),
    // a delta over DefaultBossDefsSeedFile. The boss list is realm-wide, so it lives
    // with the set, not the profile.
    public static string BossesFile(string setName) =>
        Path.Combine(GameDataSetDir(setName), "bosses.json");

    // Per-set tracked boss kill-times ({name: killed-at UTC}). Persisted so a
    // long respawn timer survives an app restart; realm-wide like BossesFile.
    public static string BossTimersFile(string setName) =>
        Path.Combine(GameDataSetDir(setName), "boss-timers.json");

    // User-writable boss-catalog seed JSON in Global/ — the curated default
    // boss list (name, rooms, realm flags, respawn type); timer values are looked up
    // from game data at runtime. BossStore falls back to this when the active set has
    // no per-set overlay. Bootstrapped from BundledBossDefsSeedFile on first launch.
    public static string DefaultBossDefsSeedFile =>
        Path.Combine(DataRoot, "Global", "BossDefs.seed.json");

    // Read-only bundled copy shipped next to the executable — the bootstrap source.
    public static string BundledBossDefsSeedFile { get; } =
        Path.Combine(AppContext.BaseDirectory, "Defaults", "BossDefs.seed.json");

    // Bootstrap missing seed files in Global/ by copying from the bundled
    // Defaults/ next to the executable. Called once during app startup.
    // Pre-existing user-edited Global seeds are never overwritten — to reset a
    // seed, delete the Global copy and the next launch re-bootstraps from the
    // bundled source.
    public static void EnsureGlobalSeedsBootstrapped()
    {
        Directory.CreateDirectory(Path.Combine(DataRoot, "Global"));
        TryCopySeed(BundledMonsterMessagesSeedFile, DefaultMonsterMessagesSeedFile);
        TryCopySeed(BundledTriggersSeedFile,        DefaultTriggersSeedFile);
        // The quest-defs seed is read-only — user edits live in the BBS-tier
        // overlay (BBS/{bbs}/quests.json), which resolves ABOVE the seed, so a
        // refreshed seed never clobbers customization. A first-launch-only copy
        // would freeze shipped guide updates out of existing installs (and reseeds
        // it if it's ever missing), so keep it in sync with the bundled copy.
        SyncReadOnlySeed(BundledQuestDefsSeedFile,  DefaultQuestDefsSeedFile);
        TryCopySeed(BundledBossDefsSeedFile,        DefaultBossDefsSeedFile);

        // MonsterOverlay + ItemOverlay + Messages seeds are realm-flavored —
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
            TryCopySeed(BundledMessagesSeedFile(realm),
                        MessagesSeedFile(realm));
        }
    }

    private static void TryCopySeed(string source, string destination)
    {
        if (File.Exists(destination)) return;
        if (!File.Exists(source)) return;  // dev builds may lack the bundled file — just skip.
        try { File.Copy(source, destination); }
        catch { /* best-effort; if the copy fails the store falls through to an empty seed */ }
    }

    // Like TryCopySeed, but for a read-only seed the app never writes back: keep the
    // Global copy identical to the bundled source, refreshing it whenever the two
    // differ (e.g. after an app update ships new seed content). Safe only for seeds
    // whose user customization lives in a separate overlay that resolves above the
    // seed — otherwise this would overwrite the user's edits.
    private static void SyncReadOnlySeed(string source, string destination)
    {
        if (!File.Exists(source)) return;  // dev builds may lack the bundled file — just skip.
        try
        {
            if (File.Exists(destination) &&
                File.ReadAllBytes(source).AsSpan().SequenceEqual(File.ReadAllBytes(destination)))
                return;  // already current
            File.Copy(source, destination, overwrite: true);
        }
        catch { /* best-effort; a stale copy is better than a failed launch */ }
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

    // Per-game-data-set GOTO favourites file. Keyed on the set (the realm's MDB)
    // rather than the character, so favourites follow the realm across every BBS /
    // character that points at that set — same rationale as the loop library above.
    public static string GameDataSetFavoritesFile(string setName) =>
        Path.Combine(GameDataSetDir(setName), "Favorites.json");

    // Read-only bundled nav seed for the given realm ("stock"/"paradigm"), shipped
    // next to the executable under Defaults/nav-seed/{realm}/ — a Loops/ tree of
    // .loop files plus a Favorites.json. NavSeedBootstrapper copies these into a
    // freshly-imported set of the matching realm so it arrives pre-populated with
    // base navigation loops + GOTO favourites. Additive + once-only.
    public static string BundledNavSeedDir(string realm) =>
        Path.Combine(AppContext.BaseDirectory, "Defaults", "nav-seed", realm);

    // Per-set sentinel written once the nav seed has been applied, so re-importing
    // the set (or the user deleting a seeded loop/favourite) never re-adds it.
    public static string NavSeedMarkerFile(string setName) =>
        Path.Combine(GameDataSetDir(setName), ".nav-seeded");

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

    // Per-BBS "top N" leaderboard capture history. Snapshots live at the BBS tier
    // so every character connecting to the same board reads and grows one shared
    // history — the whole point of the XP/HR calculator is a communal, player-fed
    // record of the realm's heroes.
    public static string BbsLeaderboardFile(string bbsName) =>
        Path.Combine(BbsFolder(bbsName), "leaderboard.json");

    // Per-BBS Roomba Mode settings: labeled gang-house rooms, hidden-search
    // config, and the @roomba remote-response toggle. Lives at the BBS tier (not
    // per-character) because a BBS ties to one game-data set and every character
    // on it shares the same gang house — labeling rooms once on any character
    // makes them available (and sortable/queryable) to every other character on
    // that board.
    public static string BbsRoombaFile(string bbsName) =>
        Path.Combine(BbsFolder(bbsName), "roomba.json");

    // Per-BBS Roomba item-sighting log: the last room each item was observed in
    // during a sweep, backing @roomba's replies. Separate file from
    // BbsRoombaFile — sightings update far more often (every room arrival during
    // a sweep) than the room-label settings do.
    public static string BbsRoombaItemsFile(string bbsName) =>
        Path.Combine(BbsFolder(bbsName), "roomba_items.json");

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

    // Per-character folder holding the death-log captures — one plain-text file
    // per death, a snapshot of the backscroll tail at the moment of death so the
    // "How did I Die?" viewer can replay the fatal scene long after the live
    // scrollback has buffered out. Sits under the character's ProfileFolder so it
    // travels with the profile and is scoped to the one character on the one BBS.
    public static string DeathLogsFolder(string bbsName, string characterName) =>
        Path.Combine(ProfileFolder(bbsName, characterName), "DeathLogs");

    // A single death-log file inside DeathLogsFolder. fileName is the bare
    // timestamped name stored on the owning DeathRecord.
    public static string DeathLogFile(string bbsName, string characterName, string fileName) =>
        Path.Combine(DeathLogsFolder(bbsName, characterName), fileName);

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

    // MUDPLAY_DATA_ROOT env var override (tests / CI).
    EnvironmentVariable,
}
