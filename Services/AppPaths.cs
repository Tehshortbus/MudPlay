using System.IO.Compression;
using System.Reflection;

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
    // (Linux: ~/.config/MudPlay/, Windows: %APPDATA%\MudPlay\,
    // macOS: ~/.config/MudPlay/) — the only file MudPlay writes
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

    // The "default profile" — a full CharacterProfile in the Global folder, loaded
    // on startup / New when no named character is chosen, and the template a new
    // character starts from. A session never writes it (ProfileService.Save): the
    // one thing the program still changes in it is the startup-animation setting.
    // Distinct from GlobalSettingsFile (the settings tier).
    public static string DefaultProfileFile { get; }

    // The marker that the default profile was put back to the installed defaults
    // (ProfileService.ResetDefaultProfileOnce).
    public static string DefaultProfileResetMarker => Path.Combine(DataRoot, "Global", ".default-profile-reset");

    // BBS-tier files (one per BBS).
    public static string BbsDir { get; }

    // The installed-font catalogue (InstalledFontCatalog). The fonts are this
    // machine's and a data folder can be one several machines share, so each
    // machine keeps its own.
    public static string FontCatalogueFile
    {
        get
        {
            string machine = string.Concat(
                Environment.MachineName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            return Path.Combine(DataRoot, "Global", $"font-catalogue.{machine}.json");
        }
    }

    // Debug logs from DebugLogWriter.
    public static string LogsDir { get; }

    // User-defined conversation emotes (Global tier): emotes.json manifest + the
    // copied image files. Shared across characters/BBSes and exportable as a package.
    public static string EmotesDir { get; }

    // The built-in sound tones, written here as WAV files when each is first played
    // in a run. Generated, never user data: deleting the folder just regenerates it.
    public static string SoundsDir { get; }

    static AppPaths()
    {
        // Pointer file lives at the platform's "config" location — separate from
        // the data root itself so the user can relocate data to a different
        // drive without losing the breadcrumb that tells us where it went.
        //   Linux  → $XDG_CONFIG_HOME (or ~/.config)
        //   Win    → %APPDATA% (roaming)
        //   macOS  → ~/.config
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
        EmotesDir          = Path.Combine(DataRoot, "Emotes");
        SoundsDir          = Path.Combine(DataRoot, "Sounds");

        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(GameDataRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(GlobalSettingsFile)!);
        Directory.CreateDirectory(BbsDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(EmotesDir);
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

    // Per-set Monster Messages catalogue file — one name record per Monsters
    // table row (the canonical name the room classifier resolves a display name
    // back to). Sits beside the per-set spell MessagesFile so the realm's
    // complete parser dataset travels together.
    public static string MonsterMessagesFile(string setName) =>
        Path.Combine(GameDataSetDir(setName), "monster-messages.json");

    // Where unrecognized lines used to be kept, per game-data set, before they moved
    // to the realm (RealmMessageCandidatesFile). A realm with none takes a copy.
    public static string LegacySetMessageCandidatesFile(string setName) =>
        Path.Combine(GameDataSetDir(setName), "message-candidates.json");

    // Per-set editable flavor-prefix vocabulary — the adjectives the game prepends
    // to a monster's base name ("large", "nasty", …). Sits beside the other per-set
    // parser data so the realm's vocabulary travels with it. No seed file: absent
    // this file the built-in MonsterFlavorPrefixes.DefaultPrefixes apply, and the
    // Game Data Browser writes the whole current list here once the user customizes.
    public static string FlavorPrefixesFile(string setName) =>
        Path.Combine(GameDataSetDir(setName), "flavor-prefixes.json");

    // Writable cache the embedded seed data is materialized into on launch
    // (MaterializeBundledSeeds). The seeds ship EMBEDDED in the assembly — a
    // self-contained single-file build carries them inside the exe — so there's
    // no loose Defaults/ folder to read at runtime; we extract them here (always
    // overwriting, so it tracks the current build) and every Bundled*SeedFile
    // accessor below points into this dir. That's what lets a user who only
    // swaps the exe still get fresh seed data: the new embedded copy lands here,
    // and EnsureGlobalSeedsBootstrapped syncs the read-only seeds into Global.
    public static string BundledSeedsDir =>
        Path.Combine(DataRoot, "_bundled");

    // User-writable MonsterMessages seed JSON, hosted in the XDG-resolved
    // Global/ folder. MonsterMessageStore builds the catalogue from this, layering
    // the per-set MonsterMessagesFile's delta on top. Byte-synced from
    // BundledMonsterMessagesSeedFile on every launch.
    public static string DefaultMonsterMessagesSeedFile =>
        Path.Combine(DataRoot, "Global", "MonsterMessages.seed.json");

    // Read-only bundled copy shipped next to the executable — the bootstrap source.
    public static string BundledMonsterMessagesSeedFile =>
        Path.Combine(BundledSeedsDir, "MonsterMessages.seed.json");

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
        Path.Combine(BundledSeedsDir, $"MonsterOverlay.{realm}.seed.json");

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
        Path.Combine(BundledSeedsDir, $"ItemOverlay.{realm}.seed.json");

    // Where the triggers saved "to the game data" used to live, per set, before
    // every trigger became the character's own. A profile from then takes a copy
    // (TriggerEngine).
    public static string LegacySetTriggersFile(string setName) =>
        Path.Combine(GameDataSetDir(setName), "triggers.json");


    // User-writable Messages seed JSON for the given realm flavor, hosted in the
    // XDG-resolved Global/ folder. Realm-flavored (stock / paradigm), each decoded
    // from that realm's MegaMUD messages.md — the active set's Info.json[0].Legit
    // picks which to apply (0/1 = stock, 2 = paradigm) via GameDataRealm.Resolve.
    // MessageStore builds the catalogue from this, layering the per-set MessagesFile's
    // delta on top.
    // Bootstrapped from the matching BundledMessagesSeedFile on first launch (or
    // delete the Global copy to re-bootstrap from the bundled source).
    public static string MessagesSeedFile(string realm) =>
        Path.Combine(DataRoot, "Global", $"Messages.{realm}.seed.json");

    // Read-only bundled copy of the realm's message seed, shipped next to the executable.
    public static string BundledMessagesSeedFile(string realm) =>
        Path.Combine(BundledSeedsDir, $"Messages.{realm}.seed.json");

    // User-writable Triggers seed JSON, hosted in the Global/ folder. A profile
    // that doesn't own a trigger list yet takes a copy of it
    // (TriggerEngine.TakeFirstList). Bootstrapped from BundledTriggersSeedFile on
    // first app launch if missing.
    public static string DefaultTriggersSeedFile =>
        Path.Combine(DataRoot, "Global", "Triggers.seed.json");

    // Read-only bundled copy shipped next to the executable — the bootstrap source.
    public static string BundledTriggersSeedFile =>
        Path.Combine(BundledSeedsDir, "Triggers.seed.json");

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
    public static string BundledQuestDefsSeedFile =>
        Path.Combine(BundledSeedsDir, "QuestDefs.seed.json");

    // Where the user's boss list used to live, per game-data set, before it moved
    // to the realm (RealmBossesFile). A realm with no list of its own takes a copy.
    public static string LegacySetBossesFile(string setName) =>
        Path.Combine(GameDataSetDir(setName), "bosses.json");

    // The user's quest-guide overlay for a game-data set: names, visibility and
    // edited step write-ups. QuestStore resolves it ABOVE the universal
    // DefaultQuestDefsSeedFile underlay. A guide describes the game the set holds,
    // so it is the set's, shared by every realm and character on it; which quests a
    // character has finished is kept on the character.
    public static string QuestsFile(string setName) =>
        Path.Combine(GameDataSetDir(setName), "quests.json");

    // Where the dead of a room were seen to arrive, learned from the deaths a
    // client witnesses (LearnedArrivalRoomStore). The game keeps it per room and
    // the imported tables don't carry it, so it is knowledge about the set: every
    // board and character on the set shares it. A file of its own, beside the
    // imported tables and never one of them.
    public static string LearnedArrivalRoomsFile(string setName) =>
        Path.Combine(GameDataSetDir(setName), "learned-arrival-rooms.json");

    // Where the Simulator's area rankings made on a set are kept between sessions
    // (AreaRankingFiles): one file for each character that has ranked on it, under
    // that character's BBS. A folder of its own inside the set's, so it goes when the
    // set is deleted and nothing that lists, counts, imports, copies or moves the
    // set's tables and edits sees it: those read the set's own folder, its Loops
    // folder or named files, never its other sub-folders. A ranking is one
    // character's results, so a copy of the set does not carry it.
    //
    // The files under it are named by BBS and profile. Only AreaRankingFiles builds
    // those paths, each piece through PathGuard, so the result stays inside this
    // folder.
    public static string SimulatorRankingsFolder(string setName) =>
        Path.Combine(GameDataSetDir(setName), "Simulator");

    // The realm folders whose old quest-guide file a set has already taken in.
    public static string QuestsAdoptedRealmsFile(string setName) =>
        Path.Combine(GameDataSetDir(setName), ".quests-adopted.json");

    // Where boss kill-times used to live, per game-data set, before they moved to
    // the realm (RealmBossTimersFile). Read once by RealmMigration.
    public static string LegacySetBossTimersFile(string setName) =>
        Path.Combine(GameDataSetDir(setName), "boss-timers.json");

    // User-writable boss-catalog seed JSON in Global/ — the curated default
    // boss list (name, rooms, realm flags, respawn type); timer values are looked up
    // from game data at runtime. BossStore lays the realm's overlay (RealmBossesFile)
    // over it. Bootstrapped from BundledBossDefsSeedFile on first launch.
    public static string DefaultBossDefsSeedFile =>
        Path.Combine(DataRoot, "Global", "BossDefs.seed.json");

    // Read-only bundled copy shipped next to the executable — the bootstrap source.
    public static string BundledBossDefsSeedFile =>
        Path.Combine(BundledSeedsDir, "BossDefs.seed.json");

    // Bootstrap the Global/ seed files from the seeds embedded in the assembly.
    // Called once during app startup. First MaterializeBundledSeeds extracts the
    // embedded copies into BundledSeedsDir (always overwriting, so they track the
    // running build); then each seed is either copied-if-missing (Triggers — user-
    // writable, must never be clobbered) or byte-synced (the read-only seeds, whose
    // user customization lives in a separate overlay tier that resolves ABOVE the
    // seed). Syncing is what lets a shipped seed update reach EXISTING installs:
    // pre-this-change the read-only seeds were copy-if-missing, so a user who
    // updated the app kept stale defaults forever (report: a monster that shipped
    // as Friend still showed Enemy after an update).
    public static void EnsureGlobalSeedsBootstrapped()
    {
        Directory.CreateDirectory(Path.Combine(DataRoot, "Global"));
        MaterializeBundledSeeds();

        // Read-only seeds — user edits live in a higher-resolving overlay, so a
        // refreshed seed never clobbers customization. Byte-synced so shipped
        // updates reach existing installs.
        SyncReadOnlySeed(BundledMonsterMessagesSeedFile, DefaultMonsterMessagesSeedFile);
        SyncReadOnlySeed(BundledQuestDefsSeedFile,       DefaultQuestDefsSeedFile);
        SyncReadOnlySeed(BundledBossDefsSeedFile,        DefaultBossDefsSeedFile);
        // Triggers are FULLY user-defined — the seed IS the user's universal list,
        // so it stays copy-if-missing; byte-syncing it would wipe hand-authored
        // triggers on every update.
        TryCopySeed(BundledTriggersSeedFile,             DefaultTriggersSeedFile);

        // MonsterOverlay + ItemOverlay + Messages seeds are realm-flavored — one
        // file per realm family. The active set picks which to apply via Info.Legit;
        // seed every realm we ship so the user can browse / edit any without first
        // switching active sets. All three are read-only (edits go to higher tiers /
        // the per-set file), so byte-sync them.
        foreach (string realm in new[] { "stock", "paradigm" })
        {
            SyncReadOnlySeed(BundledMonsterOverlaySeedFile(realm),
                             MonsterOverlaySeedFile(realm));
            SyncReadOnlySeed(BundledItemOverlaySeedFile(realm),
                             ItemOverlaySeedFile(realm));
            SyncReadOnlySeed(BundledMessagesSeedFile(realm),
                             MessagesSeedFile(realm));
        }
    }

    // Extract the seed data embedded in the assembly (Defaults/*.seed.json) into
    // the writable BundledSeedsDir cache, overwriting each launch so the cache
    // always matches the running build. Every Bundled*SeedFile accessor reads from
    // that cache, so the rest of the bootstrap sees the current build's seeds even
    // though nothing ships loose next to the exe. Best-effort per file.
    public static void MaterializeBundledSeeds()
    {
        ExtractEmbeddedSeeds(BundledSeedsDir);
        ExtractEmbeddedNavSeed(BundledSeedsDir);
    }

    // Write every embedded Defaults/*.seed.json resource into destDir (created if
    // needed), overwriting. Split out from MaterializeBundledSeeds so it can be
    // exercised against a scratch dir without touching the real data root. Best-
    // effort per file — a failed extract leaves the store to fall through to an
    // empty/older seed rather than crashing launch.
    public static void ExtractEmbeddedSeeds(string destDir)
    {
        Directory.CreateDirectory(destDir);
        Assembly asm = typeof(AppPaths).Assembly;
        const string marker = ".Defaults.";
        foreach (string resource in asm.GetManifestResourceNames())
        {
            int at = resource.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) continue;                       // not a Defaults/ seed resource
            string fileName = resource[(at + marker.Length)..];
            if (!fileName.EndsWith(".seed.json", StringComparison.OrdinalIgnoreCase))
                continue;                               // nav-seed zips handled separately
            try
            {
                using Stream? src = asm.GetManifestResourceStream(resource);
                if (src is null) continue;
                using FileStream dst = File.Create(Path.Combine(destDir, fileName));
                src.CopyTo(dst);
            }
            catch { /* best-effort */ }
        }
    }

    // Unzip the embedded nav-seed bundles (Defaults.nav-seed.{realm}.zip) into
    // destDir/nav-seed/{realm}/, overwriting the realm folder each launch so it
    // tracks the current build's starter Loops + Favorites. Portable: ZipArchive /
    // ExtractToDirectory are managed .NET, and the zip stores '/'-separated entries
    // that extract to the local separator on any OS.
    public static void ExtractEmbeddedNavSeed(string destDir)
    {
        Assembly asm = typeof(AppPaths).Assembly;
        const string marker = ".Defaults.nav-seed.";
        foreach (string resource in asm.GetManifestResourceNames())
        {
            int at = resource.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0 || !resource.EndsWith(".zip", StringComparison.Ordinal)) continue;
            string realm = resource[(at + marker.Length)..^".zip".Length];
            string realmDir = Path.Combine(destDir, "nav-seed", realm);
            try
            {
                using Stream? src = asm.GetManifestResourceStream(resource);
                if (src is null) continue;
                if (Directory.Exists(realmDir)) Directory.Delete(realmDir, recursive: true);
                Directory.CreateDirectory(realmDir);
                using ZipArchive zip = new(src);
                zip.ExtractToDirectory(realmDir, overwriteFiles: true);
            }
            catch { /* best-effort */ }
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

    // ----- Paths made from a name ---------
    //
    // A set, a BBS, a realm and a character are folders named by the user, and the
    // names also come out of global.json, a bbs.json and the command line. Every
    // path below that is made from one is made here, so that whatever the name, the
    // path is an entry directly inside the folder it belongs in (PathGuard.Child).
    //
    // The stores check a name before they act and report a bad one in their own
    // way; this is what stands behind them for every caller that checked nothing.
    // It cannot report a failure, so a name that would lead elsewhere gets
    // RefusedName in its place: a path inside the right folder where nothing is, so
    // a read finds nothing and a delete removes nothing, with a Warn in the log.
    private static string Named(string parent, string name, string what)
    {
        if (PathGuard.Child(parent, name) is not null) return Path.Combine(parent, name);
        PathGuard.Refuse($"A path for a {what}", what, name);
        return Path.Combine(parent, RefusedName);
    }

    // The entry a refused name is given. Plain, so it can be seen and removed like
    // any other should something ever be written to it.
    public const string RefusedName = ".refused-name";

    // Path to a single imported game-data set's directory.
    public static string GameDataSetDir(string setName) =>
        Named(GameDataRoot, setName, "game-data set name");

    // Folder holding all files for one BBS — bbs.json, its realm folders
    // (RealmsDir, where the BBS-tier override side-files live), its emote library,
    // and the profiles of every character on it.
    public static string BbsFolder(string bbsName) =>
        Named(BbsDir, bbsName, "BBS name");

    // A BBS's emote library: emotes.json plus its images. AppPaths.EmotesDir is the
    // library every BBS used before, and what a BBS with none of its own copies.
    public static string BbsEmotesDir(string bbsName) =>
        Path.Combine(BbsFolder(bbsName), "Emotes");

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

    // The GOTO favourites every character on a set once shared. Favourites are the
    // character's own now (CharacterProfile.Favorites); this file is what a
    // character from before copies on its first load, and NavSeedBootstrapper still
    // keeps the bundled starters in it for that.
    public static string GameDataSetFavoritesFile(string setName) =>
        Path.Combine(GameDataSetDir(setName), "Favorites.json");

    // Read-only bundled nav seed for the given realm ("stock"/"paradigm"), shipped
    // next to the executable under Defaults/nav-seed/{realm}/ — a Loops/ tree of
    // .loop files plus a Favorites.json. NavSeedBootstrapper copies these into a
    // freshly-imported set of the matching realm so it arrives pre-populated with
    // base navigation loops + GOTO favourites. Additive + once-only.
    public static string BundledNavSeedDir(string realm) =>
        Path.Combine(BundledSeedsDir, "nav-seed", realm);

    // Per-set ledger of nav-seed item identities (loop relative-paths + favourite
    // keys) already offered to this set. Replaces the old binary .nav-seeded marker:
    // an item in the ledger is never re-added (so a user deletion stays deleted),
    // while a NEW bundled item absent from the ledger is added on the next launch.
    public static string NavSeedLedgerFile(string setName) =>
        Path.Combine(GameDataSetDir(setName), ".nav-seed-ledger.json");

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

    // ----- Realm folder: BBS/{bbs}/Realms/{realm}/ -----
    // Everything collected while playing one realm of a BBS, shared by the
    // characters assigned to it (RealmProfile). The file helpers below take the
    // realm folder itself, which the stores are handed as their active scope.

    // Folder for one realm of a BBS.
    public static string RealmFolder(string bbsName, string realmName) =>
        Named(RealmsDir(bbsName), realmName, "realm name");

    // The realm's folder, or null when the BBS or realm name can't be a folder (a
    // bbs.json can hold any text): what is kept for the realm then holds nothing, as
    // with no BBS, instead of sharing the holding folder for refused names with every
    // other such realm and reading one board's data as another's.
    public static string? UsableRealmFolder(string bbsName, string realmName)
    {
        if (PathGuard.Child(BbsDir, bbsName) is not null
            && PathGuard.Child(RealmsDir(bbsName), realmName) is not null)
            return RealmFolder(bbsName, realmName);
        PathGuard.Refuse("Finding the active realm's data folder", "realm name", realmName);
        return null;
    }

    // The folder a BBS's realm folders sit in.
    public static string RealmsDir(string bbsName) =>
        Path.Combine(BbsFolder(bbsName), "Realms");

    // Observed players. One PlayerObservation per player ever seen on the realm,
    // so the same name on another realm (or board) counts as a different person.
    public static string RealmPlayersFile(string realmFolder) =>
        Path.Combine(realmFolder, "players.json");

    // Map-room blacklist. Entries hide their target rooms from the navigation map
    // render and the search box — typical use is hiding ganghouse / sysop-only
    // rooms behind dead-end doors that clutter the layout.
    public static string RealmRoomBlacklistFile(string realmFolder) =>
        Path.Combine(realmFolder, "room_blacklist.json");

    // "Top N" leaderboard capture history, one shared history for every character
    // on the realm — the XP/HR calculator is a communal, player-fed record of the
    // realm's heroes.
    public static string RealmLeaderboardFile(string realmFolder) =>
        Path.Combine(realmFolder, "leaderboard.json");

    // Roomba Mode settings: labeled gang-house rooms, hidden-search config, and the
    // @roomba remote-response toggle. Every character on the realm shares the same
    // gang house, so labeling rooms once on any of them serves all.
    public static string RealmRoombaFile(string realmFolder) =>
        Path.Combine(realmFolder, "roomba.json");

    // Roomba item-sighting log: the last room each item was observed in during a
    // sweep, backing @roomba's replies. Separate from RealmRoombaFile — sightings
    // update far more often than the room labels do.
    public static string RealmRoombaItemsFile(string realmFolder) =>
        Path.Combine(realmFolder, "roomba_items.json");

    // The realm's unrecognized lines — raw wire lines MessageCandidateWatcher
    // captured because they matched no message record and no registered router
    // pattern. What the characters on the realm saw, so the realm's.
    public static string RealmMessageCandidatesFile(string realmFolder) =>
        Path.Combine(realmFolder, "message-candidates.json");

    // The realm's boss list — the user-owned layer (added / removed bosses, edited
    // rooms, stop-before flags), a delta over DefaultBossDefsSeedFile. Two realms on
    // the same game data keep their own.
    public static string RealmBossesFile(string realmFolder) =>
        Path.Combine(realmFolder, "bosses.json");

    // Tracked boss kill-times ({name: killed-at UTC}), persisted so a long respawn
    // timer survives an app restart. Observed in play, so they belong to the realm.
    public static string RealmBossTimersFile(string realmFolder) =>
        Path.Combine(realmFolder, "boss-timers.json");

    // Believed copper in each stash room ({"map/room": copper}). A stash is in the
    // room for every character on the realm, so they share the tally.
    public static string RealmStashBalancesFile(string realmFolder) =>
        Path.Combine(realmFolder, "stash-balances.json");

    // The names seen on this realm for rooms the game data gives none
    // (Game.Map.LearnedRoomNameStore), each filed under the game-data set whose
    // room numbers it goes by. What such a room is called is only known from what
    // the board printed, so the names are the realm's, never written into the set.
    public static string RealmLearnedRoomNamesFile(string realmFolder) =>
        Path.Combine(realmFolder, "learned-room-names.json");

    // Where a realm's quest-guide edits used to be kept, before the guides went
    // back to the game-data set (QuestsFile). A set takes each realm's in once.
    public static string LegacyRealmQuestsFile(string realmFolder) =>
        Path.Combine(realmFolder, "quests.json");

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
        Named(BbsProfilesDir(bbsName), characterName, "profile name");

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
        Named(DeathLogsFolder(bbsName, characterName), fileName, "death-log file name");

    // Per-set game-data override side-file at the given tier. Routes to the
    // right folder: Global → DataRoot/Global, BBS (the "only for this realm" tier)
    // → the active realm's folder, Character → ProfileFolder. File name is
    // {table-lowercase}_overrides.{set}.json, e.g.
    // monster_overrides.data-v1.11p.json. The Defaults tier is read-only and
    // throws. tierScopeName is the realm FOLDER for the BBS tier and the
    // profile name for the Character tier (ignored for Global). characterBbs,
    // for the Character tier only, is the BBS the profile lives under (profiles
    // nest at BBS/{bbs}/profiles/{char}/); ignored for other tiers.
    public static string OverrideFile(SettingsTier tier, string? tierScopeName, string table, string setName, string? characterBbs = null)
    {
        string folder = tier switch
        {
            SettingsTier.Defaults  => throw new InvalidOperationException("Defaults tier is read-only — no override side-file."),
            SettingsTier.Global    => Path.Combine(DataRoot, "Global"),
            SettingsTier.Bbs       => RequireScope(tierScopeName, "Realm folder"),
            SettingsTier.Character => ProfileFolder(RequireScope(characterBbs, "Character BBS"), RequireScope(tierScopeName, "Character")),
            _ => throw new ArgumentOutOfRangeException(nameof(tier)),
        };
        // The set's name is part of the file's: one with a separator in it would put
        // the file in another folder.
        return Named(folder, $"{table.ToLowerInvariant()}_overrides.{setName}.json", "game-data set name");
    }

    private static string RequireScope(string? scope, string label)
        => string.IsNullOrWhiteSpace(scope)
            ? throw new InvalidOperationException($"OverrideFile for {label} tier requires a scope name (the BBS or profile).")
            : scope;

    // Path for a new debug log file. Caller supplies a topic; the timestamp is
    // generated at call time so concurrent loggers don't collide.
    public static string NewDebugLogFile(string topic)
    {
        // Include the process id: sibling instances launched together (e.g. via
        // --profile a,b,c) start within the same second, so a timestamp alone
        // would collide on the fixed-topic logs (program / memory) and the second
        // opener would be denied the write lock. The pid makes every instance's
        // file unique.
        string ts = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        return Path.Combine(LogsDir, $"{ts}-p{Environment.ProcessId}-{topic}.log");
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

    // MUDPLAY_DATA_ROOT env var override (CI).
    EnvironmentVariable,
}
